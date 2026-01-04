using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PriceWatcher.Bot.Services;

namespace PriceWatcher.Bot.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramBot(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotOptions>(options =>
        {
            options.Token = configuration["TELEGRAM_BOT_TOKEN"] ?? "";
            var adminIds = configuration["ADMIN_TELEGRAM_IDS"] ?? "";
            options.AdminTelegramIds = adminIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.TryParse(s.Trim(), out var id) ? id : 0)
                .Where(id => id != 0)
                .ToArray();
        });

        services.AddScoped<UpdateHandler>();
        services.AddScoped<ChartService>();
        services.AddHostedService<TelegramBotService>();

        return services;
    }
}
