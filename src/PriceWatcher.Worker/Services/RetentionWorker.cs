using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PriceWatcher.Worker.Services;

public class RetentionOptions
{
    public int RetentionDays { get; set; } = 180; // 6 months
    public int CleanupIntervalHours { get; set; } = 24;
}

public class RetentionWorker(
    IServiceProvider serviceProvider,
    IOptions<RetentionOptions> options,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RetentionWorker started. Retention: {Days} days, Cleanup interval: {Hours} hours",
            options.Value.RetentionDays, options.Value.CleanupIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldEventsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during retention cleanup");
            }

            await Task.Delay(TimeSpan.FromHours(options.Value.CleanupIntervalHours), stoppingToken);
        }
    }

    private async Task CleanupOldEventsAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Data.PriceWatcherDbContext>();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays);

        var deletedCount = await db.PriceEvents
            .Where(e => e.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedCount > 0)
        {
            logger.LogInformation("Deleted {Count} old price events older than {Cutoff}", deletedCount, cutoff);
        }
    }
}
