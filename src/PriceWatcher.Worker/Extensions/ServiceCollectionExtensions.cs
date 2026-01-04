using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriceWatcher.Worker.Services;

namespace PriceWatcher.Worker.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPriceCheckWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(options =>
        {
            options.CheckCron = configuration["CHECK_CRON"] ?? "0 8,20 * * *";
            options.TimeZone = configuration["CHECK_TIMEZONE"] ?? "Europe/Vilnius";
            options.MaxParallel = int.TryParse(configuration["MAX_PARALLEL"], out var mp) ? mp : 5;
            options.HostMinDelayMs = int.TryParse(configuration["HOST_MIN_DELAY_MS"], out var hd) ? hd : 2000;
            options.HostJitterMs = int.TryParse(configuration["HOST_JITTER_MS"], out var hj) ? hj : 500;
        });

        services.Configure<NotificationOptions>(options =>
        {
            options.BotToken = configuration["TELEGRAM_BOT_TOKEN"] ?? "";
            options.ErrorNotificationCooldownHours = int.TryParse(configuration["ERROR_NOTIFICATION_COOLDOWN_HOURS"], out var ec) ? ec : 6;
        });

        services.Configure<RetentionOptions>(options =>
        {
            options.RetentionDays = int.TryParse(configuration["RETENTION_DAYS"], out var rd) ? rd : 180;
            options.CleanupIntervalHours = int.TryParse(configuration["CLEANUP_INTERVAL_HOURS"], out var ci) ? ci : 24;
        });

        services.AddSingleton<PriceCheckQueue>();
        services.AddSingleton<NotificationService>();
        services.AddHostedService<PriceCheckWorker>();
        services.AddHostedService<RetentionWorker>();

        return services;
    }
}
