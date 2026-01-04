using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PriceWatcher.Domain.Interfaces;
using PriceWatcher.Domain.Models;
using PriceWatcher.Infrastructure.Parsing;

namespace PriceWatcher.Infrastructure.Sources;

public partial class OzonProductSource : IProductSource, IAsyncDisposable
{
    private readonly ILogger<OzonProductSource> _logger;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    public OzonProductSource(ILogger<OzonProductSource> logger)
    {
        _logger = logger;
    }

    public string SourceKey => "ozon";

    public bool CanHandle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return host.Contains("ozon.ru") || host.Contains("ozon.com");
    }

    public async Task<ProductSnapshot> FetchAsync(string url, CancellationToken ct = default)
    {
        var browser = await EnsureBrowserAsync();
        var page = await browser.NewPageAsync();

        try
        {
            _logger.LogDebug("Fetching Ozon product: {Url}", url);

            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });

            // Wait a bit for dynamic content
            await page.WaitForTimeoutAsync(Random.Shared.Next(800, 1500));

            var html = await page.ContentAsync();

            // Extract title
            var title = await ExtractTitleAsync(page);

            // Extract price using multiple strategies
            var (priceMinor, rawText) = await ExtractPriceAsync(page, html);

            var availability = priceMinor.HasValue ? Availability.InStock : Availability.Unknown;

            _logger.LogInformation(
                "Fetched Ozon product: {Title}, Price: {Price}, Availability: {Availability}",
                title, priceMinor, availability);

            return new ProductSnapshot(
                CanonicalUrl: url,
                Title: title,
                PriceMinor: priceMinor,
                Currency: "RUB",
                Availability: availability,
                CapturedAt: DateTimeOffset.UtcNow
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Ozon product: {Url}", url);
            throw;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<string> ExtractTitleAsync(IPage page)
    {
        // Try og:title first
        var ogTitle = await page.EvaluateAsync<string?>(
            "() => document.querySelector('meta[property=\"og:title\"]')?.content");

        if (!string.IsNullOrWhiteSpace(ogTitle))
            return ogTitle.Trim();

        // Fallback to document.title
        var docTitle = await page.TitleAsync();
        if (!string.IsNullOrWhiteSpace(docTitle))
        {
            // Remove common suffixes like " - купить в интернет-магазине OZON"
            var cleanTitle = OzonTitleSuffixRegex().Replace(docTitle, "").Trim();
            return cleanTitle;
        }

        return "Неизвестный товар";
    }

    private async Task<(long? PriceMinor, string? RawText)> ExtractPriceAsync(IPage page, string html)
    {
        // Strategy 1: Try to extract from JSON state
        var jsonPrice = await TryExtractFromJsonStateAsync(page, html);
        if (jsonPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from JSON state: {Price}", jsonPrice.Value);
            return (jsonPrice.Value, null);
        }

        // Strategy 2: Try JSON-LD structured data
        var jsonLdPrice = await TryExtractFromJsonLdAsync(page);
        if (jsonLdPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from JSON-LD: {Price}", jsonLdPrice.Value);
            return (jsonLdPrice.Value, null);
        }

        // Strategy 3: Try meta tags
        var metaPrice = await TryExtractFromMetaAsync(page);
        if (metaPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from meta tags: {Price}", metaPrice.Value);
            return (metaPrice.Value, null);
        }

        // Strategy 4: Try DOM selectors
        var (domPrice, rawText) = await TryExtractFromDomAsync(page);
        if (domPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from DOM: {Price} (raw: {RawText})", domPrice.Value, rawText);
            return (domPrice.Value, rawText);
        }

        _logger.LogWarning("Could not extract price from page");
        return (null, null);
    }

    private async Task<long?> TryExtractFromJsonStateAsync(IPage page, string html)
    {
        try
        {
            // Look for __NUXT_DATA__ or similar state objects
            var stateMatch = OzonStateRegex().Match(html);
            if (stateMatch.Success)
            {
                var jsonStr = stateMatch.Groups[1].Value;
                // This is a simplified extraction - real implementation would parse the state properly
            }

            // Try to find price in window.__PRELOADED_STATE__ or similar
            var price = await page.EvaluateAsync<long?>("""
                () => {
                    try {
                        // Try various state objects
                        const state = window.__NUXT_DATA__ || window.__PRELOADED_STATE__ || window.__INITIAL_STATE__;
                        if (state) {
                            const json = JSON.stringify(state);
                            const match = json.match(/"price"[:\s]*(\d+)/);
                            if (match) return parseInt(match[1]) * 100;
                        }
                    } catch {}
                    return null;
                }
            """);

            return price;
        }
        catch
        {
            return null;
        }
    }

    private async Task<long?> TryExtractFromJsonLdAsync(IPage page)
    {
        try
        {
            var price = await page.EvaluateAsync<string?>("""
                () => {
                    const scripts = document.querySelectorAll('script[type="application/ld+json"]');
                    for (const script of scripts) {
                        try {
                            const data = JSON.parse(script.textContent);
                            if (data['@type'] === 'Product' && data.offers) {
                                const offers = Array.isArray(data.offers) ? data.offers[0] : data.offers;
                                if (offers.price) return offers.price.toString();
                            }
                        } catch {}
                    }
                    return null;
                }
            """);

            return PriceParser.ParseToMinor(price);
        }
        catch
        {
            return null;
        }
    }

    private async Task<long?> TryExtractFromMetaAsync(IPage page)
    {
        try
        {
            var price = await page.EvaluateAsync<string?>("""
                () => {
                    const meta = document.querySelector('meta[property="product:price:amount"]') ||
                                 document.querySelector('meta[itemprop="price"]') ||
                                 document.querySelector('meta[name="price"]');
                    return meta?.content;
                }
            """);

            return PriceParser.ParseToMinor(price);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(long? Price, string? RawText)> TryExtractFromDomAsync(IPage page)
    {
        try
        {
            // Try various price selectors commonly used on Ozon
            var priceSelectors = new[]
            {
                "[data-widget='webPrice'] span:first-child",
                "[data-widget='webSale'] span:first-child",
                ".price-number",
                "[class*='price'] span",
                "[class*='Price'] span",
                "[data-testid='price']",
            };

            foreach (var selector in priceSelectors)
            {
                var priceText = await page.EvaluateAsync<string?>(
                    "() => { const el = document.querySelector('" + selector + "'); return el?.textContent?.trim(); }");

                if (!string.IsNullOrWhiteSpace(priceText))
                {
                    var parsed = PriceParser.ParseToMinor(priceText);
                    if (parsed.HasValue)
                        return (parsed, priceText);
                }
            }

            // Fallback: try to find any element with price-like content
            var genericPrice = await page.EvaluateAsync<string?>("""
                () => {
                    const elements = document.body.querySelectorAll('*');
                    for (const el of elements) {
                        if (el.children.length === 0) {
                            const text = el.textContent?.trim();
                            if (text && /^\d[\d\s]*₽$/.test(text)) {
                                return text;
                            }
                        }
                    }
                    return null;
                }
            """);

            if (!string.IsNullOrWhiteSpace(genericPrice))
            {
                var parsed = PriceParser.ParseToMinor(genericPrice);
                if (parsed.HasValue)
                    return (parsed, genericPrice);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is not null)
            return _browser;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser is not null)
                return _browser;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-blink-features=AutomationControlled"]
            });

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                Locale = "ru-RU",
                TimezoneId = "Europe/Vilnius",
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
            });

            // Return the default context's browser
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        _browserLock.Dispose();

        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\s*[-–—|]\s*(купить|OZON|Озон|интернет-магазин).*$", RegexOptions.IgnoreCase)]
    private static partial Regex OzonTitleSuffixRegex();

    [GeneratedRegex(@"__NUXT_DATA__\s*=\s*(\[[\s\S]*?\]);")]
    private static partial Regex OzonStateRegex();
}
