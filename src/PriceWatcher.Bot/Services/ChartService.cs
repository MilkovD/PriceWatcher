using Microsoft.EntityFrameworkCore;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Data.Entities;
using ScottPlot;

namespace PriceWatcher.Bot.Services;

public class ChartService(IDbContextFactory<PriceWatcherDbContext> dbFactory)
{
    public async Task<(byte[] PngData, HistoryStats Stats)?> GenerateHistoryChartAsync(
        int itemId,
        int days = 90,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var events = await db.PriceEvents
            .Where(e => e.TrackedItemId == itemId &&
                        e.Timestamp >= cutoff &&
                        e.PriceMinor.HasValue &&
                        (e.Kind == PriceEventKind.Change || e.Kind == PriceEventKind.Snapshot))
            .OrderBy(e => e.Timestamp)
            .Select(e => new { e.Timestamp, Price = e.PriceMinor!.Value })
            .ToListAsync(ct);

        if (events.Count == 0)
            return null;

        // Calculate stats
        var prices = events.Select(e => e.Price).ToList();
        var stats = new HistoryStats
        {
            MinPrice = prices.Min(),
            MaxPrice = prices.Max(),
            AvgPrice = (long)prices.Average(),
            LastPrice = prices[^1],
            FirstPrice = prices[0],
            PointCount = events.Count
        };

        // Generate chart
        var plot = new Plot();
        plot.Title($"История цены за {days} дней");
        plot.XLabel("Дата");
        plot.YLabel("Цена (₽)");

        var dates = events.Select(e => e.Timestamp.DateTime.ToOADate()).ToArray();
        var priceValues = events.Select(e => e.Price / 100.0).ToArray();

        var scatter = plot.Add.Scatter(dates, priceValues);
        scatter.LineWidth = 2;
        scatter.MarkerSize = events.Count <= 30 ? 8 : 4;

        // Configure X axis for dates
        plot.Axes.DateTimeTicksBottom();

        // Set Y axis to show reasonable range
        var minY = priceValues.Min() * 0.95;
        var maxY = priceValues.Max() * 1.05;
        plot.Axes.SetLimitsY(minY, maxY);

        // Generate PNG
        var pngData = plot.GetImageBytes(800, 400, ImageFormat.Png);

        return (pngData, stats);
    }
}

public record HistoryStats
{
    public long MinPrice { get; init; }
    public long MaxPrice { get; init; }
    public long AvgPrice { get; init; }
    public long FirstPrice { get; init; }
    public long LastPrice { get; init; }
    public int PointCount { get; init; }

    public decimal ChangePct => FirstPrice == 0 ? 0 : (LastPrice - FirstPrice) * 100m / FirstPrice;
    public long ChangeAbs => LastPrice - FirstPrice;
}
