using System.Diagnostics;
using Cronos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriceWatcher.Domain.Interfaces;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Data.Entities;
using PriceWatcher.Worker.Telemetry;

namespace PriceWatcher.Worker.Services;

public class WorkerOptions
{
    public string CheckCron { get; set; } = "0 8,20 * * *"; // 08:00 and 20:00
    public string TimeZone { get; set; } = "Europe/Vilnius";
    public int MaxParallel { get; set; } = 5;
    public int HostMinDelayMs { get; set; } = 2000;
    public int HostJitterMs { get; set; } = 500;
}

public class PriceCheckWorker(
    IServiceProvider serviceProvider,
    PriceCheckQueue queue,
    IOptions<WorkerOptions> options,
    ILogger<PriceCheckWorker> logger) : BackgroundService
{
    private readonly CronExpression _cronExpression = CronExpression.Parse(options.Value.CheckCron);
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
    private readonly SemaphoreSlim _parallelSemaphore = new(options.Value.MaxParallel, options.Value.MaxParallel);
    private readonly HostRateLimiter _rateLimiter = new(options.Value.HostMinDelayMs, options.Value.HostJitterMs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("PriceCheckWorker started. Cron: {Cron}, TimeZone: {TimeZone}, MaxParallel: {MaxParallel}",
            options.Value.CheckCron, options.Value.TimeZone, options.Value.MaxParallel);

        // Start queue processor
        _ = ProcessQueueAsync(stoppingToken);

        // Start scheduler
        await ScheduleChecksAsync(stoppingToken);
    }

    private async Task ScheduleChecksAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var next = _cronExpression.GetNextOccurrence(now, _timeZone);

                if (next is null)
                {
                    logger.LogWarning("No next occurrence found for cron expression");
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                    continue;
                }

                var delay = next.Value - now;
                logger.LogInformation("Next scheduled check at {NextCheck} (in {Delay})", next.Value, delay);

                await Task.Delay(delay, ct);

                if (!ct.IsCancellationRequested)
                {
                    await EnqueueAllItemsAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler");
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }

    private async Task EnqueueAllItemsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PriceWatcherDbContext>();

            var itemIds = await db.TrackedItems
                .Select(i => i.Id)
                .ToListAsync(ct);

            logger.LogInformation("Enqueueing {Count} items for scheduled check", itemIds.Count);
            queue.EnqueueRange(itemIds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error enqueueing items");
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var tasks = new List<Task>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var itemId = await queue.DequeueAsync(ct);

                await _parallelSemaphore.WaitAsync(ct);

                var task = ProcessItemAsync(itemId, ct)
                    .ContinueWith(_ => _parallelSemaphore.Release(), TaskScheduler.Default);

                tasks.Add(task);

                // Clean up completed tasks periodically
                tasks.RemoveAll(t => t.IsCompleted);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in queue processor");
            }
        }

        // Wait for remaining tasks to complete
        await Task.WhenAll(tasks);
    }

    private async Task ProcessItemAsync(int itemId, CancellationToken ct)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PriceWatcherDbContext>();
            var sourceResolver = scope.ServiceProvider.GetRequiredService<IProductSourceResolver>();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

            var item = await db.TrackedItems.FindAsync([itemId], ct);
            if (item is null)
            {
                logger.LogDebug("Item {ItemId} not found, skipping", itemId);
                return;
            }

            var source = sourceResolver.TryResolve(item.Url);
            if (source is null)
            {
                logger.LogWarning("No source found for item {ItemId} URL: {Url}", itemId, item.Url);
                return;
            }

            // Apply rate limiting
            using (await _rateLimiter.AcquireAsync(item.Url, ct))
            {
                await CheckItemAsync(db, notifications, source, item, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing item {ItemId}", itemId);
        }
    }

    private async Task CheckItemAsync(
        PriceWatcherDbContext db,
        NotificationService notifications,
        IProductSource source,
        TrackedItem item,
        CancellationToken ct)
    {
        var oldPrice = item.LastKnownPriceMinor;
        var oldState = item.State;

        using var activity = PriceWatcherMetrics.ActivitySource.StartActivity("CheckItem");
        activity?.SetTag("item.id", item.Id);
        activity?.SetTag("item.url", item.Url);
        activity?.SetTag("source", source.SourceKey);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var snapshot = await source.FetchAsync(item.Url, ct);

            item.Title = snapshot.Title;
            item.LastCheckAt = snapshot.CapturedAt;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.LastError = null;
            item.LastErrorCode = null;

            var isFirstCheckOfDay = !await db.PriceEvents
                .Where(e => e.TrackedItemId == item.Id &&
                            e.Kind == PriceEventKind.Snapshot &&
                            e.Timestamp.Date == DateTimeOffset.UtcNow.Date)
                .AnyAsync(ct);

            if (snapshot.PriceMinor.HasValue)
            {
                item.LastKnownPriceMinor = snapshot.PriceMinor;
                item.State = ItemState.Ok;

                if (oldState == ItemState.PriceMissing)
                {
                    // Price recovered
                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = PriceEventKind.Recovered,
                        PriceMinor = snapshot.PriceMinor,
                        Timestamp = snapshot.CapturedAt
                    });
                    await notifications.NotifyPriceRecoveredAsync(item, snapshot.PriceMinor.Value, ct);
                }
                else if (oldPrice.HasValue && oldPrice.Value != snapshot.PriceMinor.Value)
                {
                    // Price changed
                    var direction = snapshot.PriceMinor.Value > oldPrice.Value ? "up" : "down";
                    PriceWatcherMetrics.PriceChangesTotal.Add(1,
                        new KeyValuePair<string, object?>("direction", direction));

                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = PriceEventKind.Change,
                        PriceMinor = snapshot.PriceMinor,
                        Timestamp = snapshot.CapturedAt
                    });
                    await notifications.NotifyPriceChangeAsync(item, oldPrice, snapshot.PriceMinor, ct);
                }

                // Daily snapshot (first successful check of the day)
                if (isFirstCheckOfDay)
                {
                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = PriceEventKind.Snapshot,
                        PriceMinor = snapshot.PriceMinor,
                        Timestamp = snapshot.CapturedAt
                    });
                }
            }
            else
            {
                item.State = ItemState.PriceMissing;

                if (oldState == ItemState.Ok && oldPrice.HasValue)
                {
                    // Price became missing
                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = PriceEventKind.Missing,
                        PriceMinor = null,
                        Timestamp = snapshot.CapturedAt
                    });
                    await notifications.NotifyPriceMissingAsync(item, ct);
                }
            }

            await db.SaveChangesAsync(ct);

            // Record metrics
            stopwatch.Stop();
            PriceWatcherMetrics.ChecksTotal.Add(1, new KeyValuePair<string, object?>("source", source.SourceKey));
            PriceWatcherMetrics.CheckDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("source", source.SourceKey));

            activity?.SetTag("item.title", item.Title);
            activity?.SetTag("item.price", snapshot.PriceMinor);
            activity?.SetStatus(ActivityStatusCode.Ok);

            logger.LogDebug("Checked item {ItemId}: {Title}, Price: {Price}", item.Id, item.Title, snapshot.PriceMinor);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking item {ItemId}", item.Id);

            // Record failure metrics
            stopwatch.Stop();
            PriceWatcherMetrics.ChecksTotal.Add(1, new KeyValuePair<string, object?>("source", source.SourceKey));
            PriceWatcherMetrics.ChecksFailedTotal.Add(1,
                new KeyValuePair<string, object?>("source", source.SourceKey),
                new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
            PriceWatcherMetrics.CheckDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("source", source.SourceKey));

            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            item.State = ItemState.Failed;
            item.LastError = ex.Message;
            item.LastErrorCode = ex.GetType().Name;
            item.LastCheckAt = DateTimeOffset.UtcNow;

            db.PriceEvents.Add(new PriceEvent
            {
                TrackedItemId = item.Id,
                Kind = PriceEventKind.Failed,
                PriceMinor = null,
                Note = ex.Message,
                Timestamp = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
            await notifications.NotifyErrorAsync(item, ex.GetType().Name, ct);
        }
    }
}
