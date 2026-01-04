using PriceWatcher.Infrastructure.Parsing;

namespace PriceWatcher.Tests;

public class PriceParserTests
{
    [Theory]
    [InlineData("1 234 ₽", 123400)]
    [InlineData("1234₽", 123400)]
    [InlineData("1 234,56 ₽", 123456)]
    [InlineData("1234,56", 123456)]
    [InlineData("1 234.56", 123456)]
    [InlineData("3456 р/шт", 345600)]
    [InlineData("3456 руб", 345600)]
    [InlineData("3456 р.", 345600)]
    [InlineData("от 1 500 ₽", 150000)]
    [InlineData("999", 99900)]
    [InlineData("1 234 567 ₽", 123456700)]
    [InlineData("1,234,567", 123456700)]
    [InlineData("  2500  ", 250000)]
    [InlineData("0", 0)]
    [InlineData("99,99 ₽", 9999)]
    public void ParseToMinor_ValidPrices_ReturnsCorrectMinorUnits(string input, long expected)
    {
        var result = PriceParser.ParseToMinor(input);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("нет в наличии")]
    [InlineData("цена по запросу")]
    public void ParseToMinor_InvalidPrices_ReturnsNull(string? input)
    {
        var result = PriceParser.ParseToMinor(input);

        Assert.Null(result);
    }

    [Fact]
    public void FormatPrice_WithValue_ReturnsFormattedRubles()
    {
        // Russian culture uses non-breaking space (U+00A0) as thousands separator
        Assert.Equal("1\u00A0234 ₽", PriceParser.FormatPrice(123400L, "RUB"));
        Assert.Equal("999 ₽", PriceParser.FormatPrice(99900L, "RUB"));
    }

    [Fact]
    public void FormatPrice_WithNull_ReturnsUnknown()
    {
        Assert.Equal("Цена неизвестна", PriceParser.FormatPrice(null, "RUB"));
    }

    [Fact]
    public void ParseToMinor_NonBreakingSpace_ParsesCorrectly()
    {
        // Non-breaking space (U+00A0) is often used in formatted prices
        var input = "1\u00A0234\u00A0₽";
        var result = PriceParser.ParseToMinor(input);

        Assert.NotNull(result);
        Assert.Equal(123400, result.Value);
    }

    [Fact]
    public void ParseToMinor_MultipleSpaces_ParsesCorrectly()
    {
        var input = "1   234   567 ₽";
        var result = PriceParser.ParseToMinor(input);

        Assert.NotNull(result);
        Assert.Equal(123456700, result.Value);
    }

    [Fact]
    public void ParseToMinor_MixedSeparators_ParsesCorrectly()
    {
        // Edge case: European format with dot as thousand separator
        var input = "1.234.567";
        var result = PriceParser.ParseToMinor(input);

        Assert.NotNull(result);
        Assert.Equal(123456700, result.Value);
    }
}
