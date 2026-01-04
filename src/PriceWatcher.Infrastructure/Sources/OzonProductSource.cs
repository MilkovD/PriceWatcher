using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PriceWatcher.Domain.Interfaces;
using PriceWatcher.Domain.Models;
using PriceWatcher.Infrastructure.Parsing;

namespace PriceWatcher.Infrastructure.Sources;

public partial class OzonProductSource : IProductSource
{
    private readonly ILogger<OzonProductSource> _logger;
    private readonly HttpClient _httpClient;

    private readonly CookieContainer _cookieContainer;
    private bool _sessionEstablished;

    public OzonProductSource(ILogger<OzonProductSource> logger)
    {
        _logger = logger;

        _cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = true,
            CookieContainer = _cookieContainer,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        SetupDefaultHeaders();
    }

    private void SetupDefaultHeaders()
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua", "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Mobile", "?0");
        _httpClient.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
        _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        _httpClient.DefaultRequestHeaders.Add("Dnt", "1");
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
        _logger.LogDebug("Fetching Ozon product: {Url}", url);

        try
        {
            // Establish session first if needed
            await EnsureSessionAsync(ct);

            // Create request with Referer header
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Referer", "https://www.ozon.ru/");

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HTTP {StatusCode} for URL: {Url}", response.StatusCode, url);

                // Try one more time after re-establishing session
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _sessionEstablished = false;
                    await EnsureSessionAsync(ct);

                    using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
                    retryRequest.Headers.Add("Referer", "https://www.ozon.ru/");
                    response = await _httpClient.SendAsync(retryRequest, ct);
                }

                response.EnsureSuccessStatusCode();
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Check if we got blocked
            if (html.Contains("Доступ ограничен") || html.Contains("доступ к запрашиваемому ресурсу ограничен"))
            {
                _logger.LogWarning("Access blocked by Ozon for URL: {Url}", url);
                throw new InvalidOperationException("Access blocked by Ozon antibot protection");
            }

            // Extract title
            var title = ExtractTitle(html);

            // Extract price using multiple strategies
            var priceMinor = ExtractPrice(html);

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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error fetching Ozon product: {Url}", url);
            throw;
        }
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionEstablished)
            return;

        _logger.LogDebug("Establishing session with Ozon...");

        try
        {
            // Visit main page to get cookies
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.ozon.ru/");
            request.Headers.Add("Sec-Fetch-Site", "none");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                _sessionEstablished = true;
                _logger.LogDebug("Session established, cookies: {Count}", _cookieContainer.Count);

                // Small delay to appear more human
                await Task.Delay(Random.Shared.Next(500, 1500), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to establish session, continuing anyway");
        }
    }

    private string ExtractTitle(string html)
    {
        // Try og:title first
        var ogTitleMatch = OgTitleRegex().Match(html);
        if (ogTitleMatch.Success)
        {
            var title = System.Web.HttpUtility.HtmlDecode(ogTitleMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(title))
                return title.Trim();
        }

        // Try <title> tag
        var titleMatch = TitleTagRegex().Match(html);
        if (titleMatch.Success)
        {
            var title = System.Web.HttpUtility.HtmlDecode(titleMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(title))
            {
                // Remove common suffixes
                var cleanTitle = OzonTitleSuffixRegex().Replace(title, "").Trim();
                return cleanTitle;
            }
        }

        return "Неизвестный товар";
    }

    private long? ExtractPrice(string html)
    {
        // Strategy 1: Try JSON-LD structured data
        var jsonLdPrice = TryExtractFromJsonLd(html);
        if (jsonLdPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from JSON-LD: {Price}", jsonLdPrice.Value);
            return jsonLdPrice.Value;
        }

        // Strategy 2: Try meta tag
        var metaPrice = TryExtractFromMeta(html);
        if (metaPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from meta: {Price}", metaPrice.Value);
            return metaPrice.Value;
        }

        // Strategy 3: Try to find price in page state JSON
        var statePrice = TryExtractFromState(html);
        if (statePrice.HasValue)
        {
            _logger.LogDebug("Price extracted from state: {Price}", statePrice.Value);
            return statePrice.Value;
        }

        // Strategy 4: Try regex pattern for price
        var regexPrice = TryExtractFromRegex(html);
        if (regexPrice.HasValue)
        {
            _logger.LogDebug("Price extracted from regex: {Price}", regexPrice.Value);
            return regexPrice.Value;
        }

        _logger.LogWarning("Could not extract price from page");
        return null;
    }

    private long? TryExtractFromJsonLd(string html)
    {
        try
        {
            var jsonLdMatches = JsonLdRegex().Matches(html);
            foreach (Match match in jsonLdMatches)
            {
                var json = match.Groups[1].Value;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("@type", out var typeEl) &&
                        typeEl.GetString() == "Product" &&
                        root.TryGetProperty("offers", out var offersEl))
                    {
                        JsonElement offer;
                        if (offersEl.ValueKind == JsonValueKind.Array)
                            offer = offersEl[0];
                        else
                            offer = offersEl;

                        if (offer.TryGetProperty("price", out var priceEl))
                        {
                            var priceStr = priceEl.ValueKind == JsonValueKind.Number
                                ? priceEl.GetDecimal().ToString()
                                : priceEl.GetString();
                            return PriceParser.ParseToMinor(priceStr);
                        }
                    }
                }
                catch
                {
                    // Try next JSON-LD block
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    private long? TryExtractFromMeta(string html)
    {
        var metaMatch = MetaPriceRegex().Match(html);
        if (metaMatch.Success)
        {
            return PriceParser.ParseToMinor(metaMatch.Groups[1].Value);
        }
        return null;
    }

    private long? TryExtractFromState(string html)
    {
        try
        {
            // Look for price in various state patterns
            var pricePatterns = new[]
            {
                PriceInStateRegex1(),
                PriceInStateRegex2(),
                PriceInStateRegex3()
            };

            foreach (var pattern in pricePatterns)
            {
                var match = pattern.Match(html);
                if (match.Success)
                {
                    var priceStr = match.Groups[1].Value;
                    return PriceParser.ParseToMinor(priceStr);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    private long? TryExtractFromRegex(string html)
    {
        // Look for price patterns like "1 234 ₽" or "1234₽"
        var priceMatch = PriceDisplayRegex().Match(html);
        if (priceMatch.Success)
        {
            return PriceParser.ParseToMinor(priceMatch.Groups[0].Value);
        }
        return null;
    }

    [GeneratedRegex(@"\s*[-–—|]\s*(купить|OZON|Озон|интернет-магазин).*$", RegexOptions.IgnoreCase)]
    private static partial Regex OzonTitleSuffixRegex();

    [GeneratedRegex(@"<meta[^>]+property=""og:title""[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleRegex();

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTagRegex();

    [GeneratedRegex(@"<script[^>]+type=""application/ld\+json""[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdRegex();

    [GeneratedRegex(@"<meta[^>]+(?:property=""product:price:amount""|itemprop=""price""|name=""price"")[^>]+content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaPriceRegex();

    [GeneratedRegex(@"""price""\s*:\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceInStateRegex1();

    [GeneratedRegex(@"""cardPrice""\s*:\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceInStateRegex2();

    [GeneratedRegex(@"""finalPrice""\s*:\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceInStateRegex3();

    [GeneratedRegex(@"\d[\d\s]*\s*₽")]
    private static partial Regex PriceDisplayRegex();
}
