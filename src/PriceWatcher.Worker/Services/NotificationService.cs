using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Data.Entities;
using PriceWatcher.Infrastructure.Parsing;
using PriceWatcher.Worker.Telemetry;
using Telegram.Bot;

namespace PriceWatcher.Worker.Services;

public class NotificationOptions
{
    public string BotToken { get; set; } = string.Empty;
    public int ErrorNotificationCooldownHours { get; set; } = 6;
}

public class NotificationService(
    IDbContextFactory<PriceWatcherDbContext> dbFactory,
    IOptions<NotificationOptions> options,
    ILogger<NotificationService> logger)
{
    private readonly ITelegramBotClient? _bot = string.IsNullOrEmpty(options.Value.BotToken)
        ? null
        : new TelegramBotClient(options.Value.BotToken);

    private readonly int _errorCooldownHours = options.Value.ErrorNotificationCooldownHours;

    public async Task NotifyPriceChangeAsync(TrackedItem item, long? oldPrice, long? newPrice, CancellationToken ct = default)
    {
        if (_bot is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FindAsync([item.UserId], ct);
            if (user is null) return;

            var oldPriceText = PriceParser.FormatPrice(oldPrice);
            var newPriceText = PriceParser.FormatPrice(newPrice);

            var diff = (newPrice ?? 0) - (oldPrice ?? 0);
            var sign = diff > 0 ? "+" : "";
            var emoji = diff > 0 ? "📈" : "📉";

            var message = $"{emoji} Цена изменилась!\n\n" +
                          $"📦 {item.Title}\n" +
                          $"💰 {oldPriceText} → {newPriceText}\n" +
                          $"📊 Изменение: {sign}{diff / 100m:N0} ₽\n\n" +
                          $"🔗 {item.Url}";

            await _bot.SendMessage(user.TelegramUserId, message, cancellationToken: ct);
            PriceWatcherMetrics.NotificationsTotal.Add(1, new KeyValuePair<string, object?>("type", "price_change"));
            logger.LogInformation("Sent price change notification to {UserId} for item {ItemId}", user.TelegramUserId, item.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send price change notification for item {ItemId}", item.Id);
        }
    }

    public async Task NotifyPriceMissingAsync(TrackedItem item, CancellationToken ct = default)
    {
        if (_bot is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FindAsync([item.UserId], ct);
            if (user is null) return;

            var message = $"⚠️ Цена недоступна\n\n" +
                          $"📦 {item.Title}\n\n" +
                          $"Возможно, товар закончился или страница изменилась.\n\n" +
                          $"🔗 {item.Url}";

            await _bot.SendMessage(user.TelegramUserId, message, cancellationToken: ct);
            PriceWatcherMetrics.NotificationsTotal.Add(1, new KeyValuePair<string, object?>("type", "price_missing"));
            logger.LogInformation("Sent price missing notification to {UserId} for item {ItemId}", user.TelegramUserId, item.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send price missing notification for item {ItemId}", item.Id);
        }
    }

    public async Task NotifyPriceRecoveredAsync(TrackedItem item, long newPrice, CancellationToken ct = default)
    {
        if (_bot is null) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FindAsync([item.UserId], ct);
            if (user is null) return;

            var priceText = PriceParser.FormatPrice(newPrice);

            var message = $"✅ Цена вернулась!\n\n" +
                          $"📦 {item.Title}\n" +
                          $"💰 Цена: {priceText}\n\n" +
                          $"🔗 {item.Url}";

            await _bot.SendMessage(user.TelegramUserId, message, cancellationToken: ct);
            PriceWatcherMetrics.NotificationsTotal.Add(1, new KeyValuePair<string, object?>("type", "price_recovered"));
            logger.LogInformation("Sent price recovered notification to {UserId} for item {ItemId}", user.TelegramUserId, item.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send price recovered notification for item {ItemId}", item.Id);
        }
    }

    public async Task NotifyErrorAsync(TrackedItem item, string errorCode, CancellationToken ct = default)
    {
        if (_bot is null) return;

        // Anti-spam check
        if (item.LastErrorNotifiedAt.HasValue &&
            item.LastErrorCode == errorCode &&
            (DateTimeOffset.UtcNow - item.LastErrorNotifiedAt.Value).TotalHours < _errorCooldownHours)
        {
            logger.LogDebug("Skipping error notification for item {ItemId} due to cooldown", item.Id);
            return;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.Users.FindAsync([item.UserId], ct);
            if (user is null) return;

            var message = $"❌ Ошибка проверки\n\n" +
                          $"📦 {item.Title}\n\n" +
                          $"Не удалось проверить цену. Бот попробует снова позже.\n\n" +
                          $"🔗 {item.Url}";

            await _bot.SendMessage(user.TelegramUserId, message, cancellationToken: ct);
            PriceWatcherMetrics.NotificationsTotal.Add(1, new KeyValuePair<string, object?>("type", "error"));

            // Update last notified time
            var dbItem = await db.TrackedItems.FindAsync([item.Id], ct);
            if (dbItem is not null)
            {
                dbItem.LastErrorNotifiedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            logger.LogInformation("Sent error notification to {UserId} for item {ItemId}", user.TelegramUserId, item.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send error notification for item {ItemId}", item.Id);
        }
    }
}
