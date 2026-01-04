using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PriceWatcher.Bot.Services;

public class BotOptions
{
    public string Token { get; set; } = string.Empty;
    public long[] AdminTelegramIds { get; set; } = [];
}

public class TelegramBotService(
    IServiceProvider serviceProvider,
    IOptions<BotOptions> options,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private readonly ITelegramBotClient _bot = new TelegramBotClient(options.Value.Token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
        };

        logger.LogInformation("Starting Telegram bot polling");

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: stoppingToken
        );

        var me = await _bot.GetMe(stoppingToken);
        logger.LogInformation("Bot started: @{Username}", me.Username);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<UpdateHandler>();
            await handler.HandleAsync(bot, update, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        logger.LogError(exception, "Telegram bot error from {Source}", source);
        return Task.CompletedTask;
    }
}
