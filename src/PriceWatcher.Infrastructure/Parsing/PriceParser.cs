using System.Globalization;
using System.Text.RegularExpressions;

namespace PriceWatcher.Infrastructure.Parsing;

public static partial class PriceParser
{
    /// <summary>
    /// Parses a price string and returns the value in minor units (kopecks).
    /// Returns null if parsing fails.
    /// </summary>
    public static long? ParseToMinor(string? priceText)
    {
        if (string.IsNullOrWhiteSpace(priceText))
            return null;

        // Remove currency symbols and common suffixes
        var cleaned = priceText
            .Replace("₽", "")
            .Replace("руб", "", StringComparison.OrdinalIgnoreCase)
            .Replace("р.", "")
            .Replace("р/шт", "")
            .Replace("/шт", "")
            .Replace("от", "", StringComparison.OrdinalIgnoreCase)
            .Replace("до", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        // Remove all whitespace (including non-breaking spaces)
        cleaned = WhitespaceRegex().Replace(cleaned, "");

        if (string.IsNullOrEmpty(cleaned))
            return null;

        // Handle formats like "1234,56" (comma as decimal separator)
        if (cleaned.Contains(',') && !cleaned.Contains('.'))
        {
            var parts = cleaned.Split(',');
            if (parts.Length == 2 && parts[1].Length <= 2)
            {
                cleaned = cleaned.Replace(',', '.');
            }
            else
            {
                // Comma is thousand separator
                cleaned = cleaned.Replace(",", "");
            }
        }

        // Handle formats like "1.234.567" (dot as thousand separator)
        var dotCount = cleaned.Count(c => c == '.');
        if (dotCount > 1)
        {
            cleaned = cleaned.Replace(".", "");
        }

        // Try to parse as decimal
        if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
        {
            // Convert to minor units (kopecks)
            return (long)Math.Round(price * 100);
        }

        // Try to extract just digits
        var digits = DigitsOnlyRegex().Replace(cleaned, "");
        if (!string.IsNullOrEmpty(digits) && long.TryParse(digits, out var intPrice))
        {
            // Assume it's already in rubles (whole number)
            return intPrice * 100;
        }

        return null;
    }

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Formats a price in minor units to a human-readable string.
    /// </summary>
    public static string FormatPrice(long? priceMinor, string currency = "RUB")
    {
        if (!priceMinor.HasValue)
            return "Цена неизвестна";

        var price = priceMinor.Value / 100m;
        return currency switch
        {
            "RUB" => $"{price.ToString("N0", RuCulture)} ₽",
            "USD" => $"${price.ToString("N2", CultureInfo.InvariantCulture)}",
            "EUR" => $"€{price.ToString("N2", CultureInfo.InvariantCulture)}",
            _ => $"{price.ToString("N2", CultureInfo.InvariantCulture)} {currency}"
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\d]")]
    private static partial Regex DigitsOnlyRegex();
}
