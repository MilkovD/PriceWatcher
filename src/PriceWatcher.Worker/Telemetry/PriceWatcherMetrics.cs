using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PriceWatcher.Worker.Telemetry;

public static class PriceWatcherMetrics
{
    public const string ServiceName = "PriceWatcher";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Counters
    public static readonly Counter<long> ChecksTotal = Meter.CreateCounter<long>(
        "pricewatcher.checks.total",
        description: "Total number of price checks performed");

    public static readonly Counter<long> ChecksFailedTotal = Meter.CreateCounter<long>(
        "pricewatcher.checks.failed.total",
        description: "Total number of failed price checks");

    public static readonly Counter<long> NotificationsTotal = Meter.CreateCounter<long>(
        "pricewatcher.notifications.total",
        description: "Total number of notifications sent");

    public static readonly Counter<long> PriceChangesTotal = Meter.CreateCounter<long>(
        "pricewatcher.price_changes.total",
        description: "Total number of price changes detected");

    // Histogram for check duration
    public static readonly Histogram<double> CheckDurationMs = Meter.CreateHistogram<double>(
        "pricewatcher.check.duration.ms",
        unit: "ms",
        description: "Duration of price check operations in milliseconds");

    // Gauge for queue size (using ObservableGauge with callback)
    private static Func<int>? _queueSizeCallback;

    public static void RegisterQueueSizeGauge(Func<int> callback)
    {
        _queueSizeCallback = callback;
        Meter.CreateObservableGauge(
            "pricewatcher.queue.size",
            () => _queueSizeCallback?.Invoke() ?? 0,
            description: "Current number of items in the check queue");
    }
}
