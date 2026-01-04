using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PriceWatcher.Domain.Interfaces;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Data.Entities;
using PriceWatcher.Infrastructure.Parsing;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace PriceWatcher.Bot.Services;

public class UpdateHandler(
    PriceWatcherDbContext db,
    IProductSourceResolver sourceResolver,
    ChartService chartService,
    IOptions<BotOptions> options,
    ILogger<UpdateHandler> logger)
{
    private readonly long[] _adminIds = options.Value.AdminTelegramIds;

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is { } message)
        {
            await HandleMessageAsync(bot, message, ct);
        }
        else if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(bot, callback, ct);
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message message, CancellationToken ct)
    {
        if (message.Text is not { } text)
            return;

        var chatId = message.Chat.Id;
        var userId = message.From?.Id ?? 0;

        logger.LogDebug("Received message from {UserId}: {Text}", userId, text);

        var user = await EnsureUserAsync(userId, ct);

        if (text.StartsWith("/start"))
        {
            await HandleStartAsync(bot, chatId, user, ct);
        }
        else if (text.StartsWith("/add"))
        {
            await HandleAddCommandAsync(bot, chatId, user, text, ct);
        }
        else if (text.StartsWith("/list") || text == "Мои товары")
        {
            await HandleListAsync(bot, chatId, user, ct);
        }
        else if (text.StartsWith("/admin"))
        {
            await HandleAdminAsync(bot, chatId, user, text, ct);
        }
        else if (text == "Добавить товар")
        {
            await bot.SendMessage(chatId, "Отправьте ссылку на товар с Ozon:", cancellationToken: ct);
        }
        else if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
                 (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            await HandleUrlAsync(bot, chatId, user, text.Trim(), ct);
        }
    }

    private async Task<Infrastructure.Data.Entities.User> EnsureUserAsync(long telegramUserId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramUserId, ct);
        if (user is null)
        {
            var isAdmin = _adminIds.Contains(telegramUserId);
            user = new Infrastructure.Data.Entities.User
            {
                TelegramUserId = telegramUserId,
                Role = isAdmin ? UserRole.Admin : UserRole.User
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("New user registered: {TelegramUserId}, Admin: {IsAdmin}", telegramUserId, isAdmin);
        }
        return user;
    }

    private async Task HandleStartAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, CancellationToken ct)
    {
        var keyboard = new ReplyKeyboardMarkup(
        [
            [new KeyboardButton("Мои товары")],
            [new KeyboardButton("Добавить товар")]
        ])
        {
            ResizeKeyboard = true
        };

        var roleText = user.Role == UserRole.Admin ? " (Админ)" : "";
        await bot.SendMessage(
            chatId,
            $"Привет! Я бот для отслеживания цен на товары.{roleText}\n\n" +
            "Команды:\n" +
            "/add <url> - добавить товар по ссылке\n" +
            "/list - список ваших товаров\n\n" +
            "Или просто отправьте ссылку на товар.",
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    private async Task HandleAddCommandAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await bot.SendMessage(chatId, "Укажите ссылку на товар:\n/add https://ozon.ru/...", cancellationToken: ct);
            return;
        }

        await HandleUrlAsync(bot, chatId, user, parts[1].Trim(), ct);
    }

    private async Task HandleUrlAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            await bot.SendMessage(chatId, "Неверный формат ссылки.", cancellationToken: ct);
            return;
        }

        var normalizedUrl = sourceResolver.NormalizeUrl(url);
        var source = sourceResolver.TryResolve(normalizedUrl);

        if (source is null)
        {
            await bot.SendMessage(
                chatId,
                "Пока поддерживается только Ozon.\nОтправьте ссылку на товар с ozon.ru",
                cancellationToken: ct
            );
            return;
        }

        var existingItem = await db.TrackedItems
            .FirstOrDefaultAsync(i => i.UserId == user.Id && i.Url == normalizedUrl, ct);

        if (existingItem is not null)
        {
            await bot.SendMessage(chatId, "Этот товар уже добавлен в ваш список.", cancellationToken: ct);
            return;
        }

        await bot.SendMessage(chatId, "Загружаю информацию о товаре...", cancellationToken: ct);

        try
        {
            var snapshot = await source.FetchAsync(normalizedUrl, ct);

            var item = new TrackedItem
            {
                UserId = user.Id,
                Url = normalizedUrl,
                SourceKey = source.SourceKey,
                Title = snapshot.Title,
                State = snapshot.PriceMinor.HasValue ? ItemState.Ok : ItemState.PriceMissing,
                LastKnownPriceMinor = snapshot.PriceMinor,
                LastCheckAt = snapshot.CapturedAt
            };

            db.TrackedItems.Add(item);

            if (snapshot.PriceMinor.HasValue)
            {
                db.PriceEvents.Add(new PriceEvent
                {
                    TrackedItem = item,
                    Kind = PriceEventKind.Snapshot,
                    PriceMinor = snapshot.PriceMinor,
                    Timestamp = snapshot.CapturedAt
                });
            }

            await db.SaveChangesAsync(ct);

            var priceText = PriceParser.FormatPrice(snapshot.PriceMinor);
            await bot.SendMessage(
                chatId,
                $"✅ Товар добавлен!\n\n" +
                $"📦 {snapshot.Title}\n" +
                $"💰 Цена: {priceText}\n\n" +
                "Используйте /list для просмотра товаров.",
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching product: {Url}", normalizedUrl);

            var item = new TrackedItem
            {
                UserId = user.Id,
                Url = normalizedUrl,
                SourceKey = source.SourceKey,
                Title = "Не удалось загрузить",
                State = ItemState.Failed,
                LastError = ex.Message,
                LastErrorCode = ex.GetType().Name,
                LastCheckAt = DateTimeOffset.UtcNow
            };

            db.TrackedItems.Add(item);
            await db.SaveChangesAsync(ct);

            await bot.SendMessage(
                chatId,
                "⚠️ Товар добавлен, но не удалось загрузить информацию.\n" +
                "Возможно, страница временно недоступна.\n\n" +
                "Бот попробует загрузить данные при следующей проверке.",
                cancellationToken: ct
            );
        }
    }

    private async Task HandleListAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, CancellationToken ct)
    {
        var items = await db.TrackedItems
            .Where(i => i.UserId == user.Id)
            .OrderByDescending(i => i.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await bot.SendMessage(
                chatId,
                "У вас пока нет отслеживаемых товаров.\n\nОтправьте ссылку на товар с Ozon, чтобы добавить.",
                cancellationToken: ct
            );
            return;
        }

        var buttons = items.Select(item =>
        {
            var priceText = item.LastKnownPriceMinor.HasValue
                ? $" - {item.LastKnownPriceMinor.Value / 100m:N0} ₽"
                : "";
            var stateIcon = item.State switch
            {
                ItemState.Ok => "",
                ItemState.PriceMissing => " ⚠️",
                ItemState.Failed => " ❌",
                _ => ""
            };
            var title = item.Title.Length > 40 ? item.Title[..37] + "..." : item.Title;
            return new[] { InlineKeyboardButton.WithCallbackData($"{title}{priceText}{stateIcon}", $"item:{item.Id}") };
        }).ToArray();

        var keyboard = new InlineKeyboardMarkup(buttons);

        await bot.SendMessage(
            chatId,
            $"Ваши товары ({items.Count}):",
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    private async Task HandleCallbackAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        if (callback.Message is null || callback.From is null)
            return;

        var chatId = callback.Message.Chat.Id;
        var userId = callback.From.Id;
        var data = callback.Data ?? "";

        var user = await EnsureUserAsync(userId, ct);

        if (data.StartsWith("item:"))
        {
            await HandleItemCallbackAsync(bot, chatId, user, data, callback.Id, ct);
        }
        else if (data.StartsWith("check:"))
        {
            await HandleCheckCallbackAsync(bot, chatId, user, data, callback.Id, ct);
        }
        else if (data.StartsWith("delete:"))
        {
            await HandleDeleteCallbackAsync(bot, chatId, user, data, callback.Id, ct);
        }
        else if (data.StartsWith("confirm_delete:"))
        {
            await HandleConfirmDeleteAsync(bot, chatId, user, data, callback.Id, ct);
        }
        else if (data.StartsWith("history:"))
        {
            await HandleHistoryCallbackAsync(bot, chatId, user, data, callback.Id, ct);
        }

        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
    }

    private async Task HandleItemCallbackAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string data, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data.AsSpan(5), out var itemId))
            return;

        var item = await db.TrackedItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == user.Id, ct);
        if (item is null)
        {
            await bot.SendMessage(chatId, "Товар не найден.", cancellationToken: ct);
            return;
        }

        var priceText = PriceParser.FormatPrice(item.LastKnownPriceMinor);

        var stateText = item.State switch
        {
            ItemState.Ok => "✅ OK",
            ItemState.PriceMissing => "⚠️ Цена недоступна",
            ItemState.Failed => "❌ Ошибка",
            _ => "Неизвестно"
        };

        var lastCheckText = item.LastCheckAt.HasValue
            ? item.LastCheckAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "Не проверялся";

        var text = $"📦 {item.Title}\n\n" +
                   $"💰 Цена: {priceText}\n" +
                   $"📊 Статус: {stateText}\n" +
                   $"🕐 Последняя проверка: {lastCheckText}\n\n" +
                   $"🔗 {item.Url}";

        var keyboard = new InlineKeyboardMarkup(
        [
            [InlineKeyboardButton.WithCallbackData("🔄 Проверить сейчас", $"check:{item.Id}")],
            [InlineKeyboardButton.WithCallbackData("📈 История", $"history:{item.Id}:90")],
            [InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"delete:{item.Id}")]
        ]);

        await bot.SendMessage(chatId, text, replyMarkup: keyboard, parseMode: ParseMode.None, cancellationToken: ct);
    }

    private async Task HandleCheckCallbackAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string data, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data.AsSpan(6), out var itemId))
            return;

        var item = await db.TrackedItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == user.Id, ct);
        if (item is null)
        {
            await bot.SendMessage(chatId, "Товар не найден.", cancellationToken: ct);
            return;
        }

        var source = sourceResolver.TryResolve(item.Url);
        if (source is null)
        {
            await bot.SendMessage(chatId, "Источник не поддерживается.", cancellationToken: ct);
            return;
        }

        await bot.SendMessage(chatId, "Проверяю цену...", cancellationToken: ct);

        try
        {
            var snapshot = await source.FetchAsync(item.Url, ct);
            var oldPrice = item.LastKnownPriceMinor;

            item.Title = snapshot.Title;
            item.LastCheckAt = snapshot.CapturedAt;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.LastError = null;
            item.LastErrorCode = null;

            if (snapshot.PriceMinor.HasValue)
            {
                if (!oldPrice.HasValue || oldPrice.Value != snapshot.PriceMinor.Value)
                {
                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = oldPrice.HasValue ? PriceEventKind.Change : PriceEventKind.Recovered,
                        PriceMinor = snapshot.PriceMinor,
                        Timestamp = snapshot.CapturedAt
                    });
                }

                item.LastKnownPriceMinor = snapshot.PriceMinor;
                item.State = ItemState.Ok;
            }
            else
            {
                if (oldPrice.HasValue)
                {
                    db.PriceEvents.Add(new PriceEvent
                    {
                        TrackedItemId = item.Id,
                        Kind = PriceEventKind.Missing,
                        PriceMinor = null,
                        Timestamp = snapshot.CapturedAt
                    });
                }
                item.State = ItemState.PriceMissing;
            }

            await db.SaveChangesAsync(ct);

            var priceText = PriceParser.FormatPrice(snapshot.PriceMinor);
            var changeText = "";
            if (oldPrice.HasValue && snapshot.PriceMinor.HasValue && oldPrice.Value != snapshot.PriceMinor.Value)
            {
                var diff = snapshot.PriceMinor.Value - oldPrice.Value;
                var sign = diff > 0 ? "+" : "";
                changeText = $"\n📉 Изменение: {sign}{diff / 100m:N0} ₽";
            }

            await bot.SendMessage(
                chatId,
                $"✅ Проверка завершена\n\n" +
                $"📦 {snapshot.Title}\n" +
                $"💰 Цена: {priceText}{changeText}",
                cancellationToken: ct
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking item {ItemId}", itemId);
            item.State = ItemState.Failed;
            item.LastError = ex.Message;
            item.LastErrorCode = ex.GetType().Name;
            item.LastCheckAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await bot.SendMessage(
                chatId,
                "❌ Не удалось проверить товар.\n" +
                "Возможно, страница временно недоступна.",
                cancellationToken: ct
            );
        }
    }

    private async Task HandleDeleteCallbackAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string data, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data.AsSpan(7), out var itemId))
            return;

        var item = await db.TrackedItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == user.Id, ct);
        if (item is null)
            return;

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("Да, удалить", $"confirm_delete:{item.Id}"),
                InlineKeyboardButton.WithCallbackData("Отмена", $"item:{item.Id}")
            ]
        ]);

        await bot.SendMessage(
            chatId,
            $"Вы уверены, что хотите удалить товар \"{item.Title}\"?",
            replyMarkup: keyboard,
            cancellationToken: ct
        );
    }

    private async Task HandleConfirmDeleteAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string data, string callbackId, CancellationToken ct)
    {
        if (!int.TryParse(data.AsSpan(15), out var itemId))
            return;

        var item = await db.TrackedItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == user.Id, ct);
        if (item is null)
            return;

        db.TrackedItems.Remove(item);
        await db.SaveChangesAsync(ct);

        await bot.SendMessage(chatId, $"Товар \"{item.Title}\" удалён.", cancellationToken: ct);
    }

    private async Task HandleHistoryCallbackAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string data, string callbackId, CancellationToken ct)
    {
        var parts = data.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var itemId) || !int.TryParse(parts[2], out var days))
            return;

        var item = await db.TrackedItems.FirstOrDefaultAsync(i => i.Id == itemId && i.UserId == user.Id, ct);
        if (item is null)
            return;

        await bot.SendMessage(chatId, $"Строю график для \"{item.Title}\"...", cancellationToken: ct);

        try
        {
            var result = await chartService.GenerateHistoryChartAsync(itemId, days, ct);

            if (result is null)
            {
                await bot.SendMessage(
                    chatId,
                    "Нет данных для построения графика.\nИстория цен появится после нескольких проверок.",
                    cancellationToken: ct
                );
                return;
            }

            var (pngData, stats) = result.Value;

            var sign = stats.ChangeAbs >= 0 ? "+" : "";
            var changeEmoji = stats.ChangeAbs > 0 ? "📈" : (stats.ChangeAbs < 0 ? "📉" : "➡️");

            var caption = $"📊 История цены за {days} дней\n\n" +
                          $"📦 {item.Title}\n\n" +
                          $"📉 Мин: {stats.MinPrice / 100m:N0} ₽\n" +
                          $"📈 Макс: {stats.MaxPrice / 100m:N0} ₽\n" +
                          $"📊 Средняя: {stats.AvgPrice / 100m:N0} ₽\n" +
                          $"💰 Текущая: {stats.LastPrice / 100m:N0} ₽\n\n" +
                          $"{changeEmoji} Изменение: {sign}{stats.ChangeAbs / 100m:N0} ₽ ({sign}{stats.ChangePct:F1}%)\n" +
                          $"📍 Точек данных: {stats.PointCount}";

            using var stream = new MemoryStream(pngData);
            var inputFile = InputFile.FromStream(stream, "chart.png");

            var keyboard = new InlineKeyboardMarkup(
            [
                [
                    InlineKeyboardButton.WithCallbackData("30 дней", $"history:{itemId}:30"),
                    InlineKeyboardButton.WithCallbackData("90 дней", $"history:{itemId}:90"),
                    InlineKeyboardButton.WithCallbackData("180 дней", $"history:{itemId}:180")
                ]
            ]);

            await bot.SendPhoto(chatId, inputFile, caption: caption, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating history chart for item {ItemId}", itemId);
            await bot.SendMessage(chatId, "Не удалось построить график.", cancellationToken: ct);
        }
    }

    private async Task HandleAdminAsync(ITelegramBotClient bot, long chatId, Infrastructure.Data.Entities.User user, string text, CancellationToken ct)
    {
        if (user.Role != UserRole.Admin)
        {
            await bot.SendMessage(chatId, "Эта команда доступна только администраторам.", cancellationToken: ct);
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await bot.SendMessage(
                chatId,
                "Админ-команды:\n" +
                "/admin users - список пользователей\n" +
                "/admin promote <telegramId> - повысить до админа\n" +
                "/admin demote <telegramId> - понизить до пользователя",
                cancellationToken: ct
            );
            return;
        }

        switch (parts[1].ToLower())
        {
            case "users":
                await HandleAdminUsersAsync(bot, chatId, ct);
                break;
            case "promote" when parts.Length >= 3 && long.TryParse(parts[2], out var promoteId):
                await HandleAdminPromoteAsync(bot, chatId, promoteId, ct);
                break;
            case "demote" when parts.Length >= 3 && long.TryParse(parts[2], out var demoteId):
                await HandleAdminDemoteAsync(bot, chatId, demoteId, ct);
                break;
            default:
                await bot.SendMessage(chatId, "Неверная команда.", cancellationToken: ct);
                break;
        }
    }

    private async Task HandleAdminUsersAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        var users = await db.Users
            .Select(u => new
            {
                u.TelegramUserId,
                u.Role,
                ItemCount = u.TrackedItems.Count
            })
            .ToListAsync(ct);

        if (users.Count == 0)
        {
            await bot.SendMessage(chatId, "Пользователей нет.", cancellationToken: ct);
            return;
        }

        var lines = users.Select(u =>
        {
            var roleIcon = u.Role == UserRole.Admin ? "👑" : "👤";
            return $"{roleIcon} {u.TelegramUserId} - {u.ItemCount} товаров";
        });

        await bot.SendMessage(chatId, $"Пользователи ({users.Count}):\n\n" + string.Join("\n", lines), cancellationToken: ct);
    }

    private async Task HandleAdminPromoteAsync(ITelegramBotClient bot, long chatId, long telegramId, CancellationToken ct)
    {
        var targetUser = await db.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramId, ct);
        if (targetUser is null)
        {
            await bot.SendMessage(chatId, "Пользователь не найден.", cancellationToken: ct);
            return;
        }

        targetUser.Role = UserRole.Admin;
        await db.SaveChangesAsync(ct);
        await bot.SendMessage(chatId, $"Пользователь {telegramId} повышен до админа.", cancellationToken: ct);
    }

    private async Task HandleAdminDemoteAsync(ITelegramBotClient bot, long chatId, long telegramId, CancellationToken ct)
    {
        var targetUser = await db.Users.FirstOrDefaultAsync(u => u.TelegramUserId == telegramId, ct);
        if (targetUser is null)
        {
            await bot.SendMessage(chatId, "Пользователь не найден.", cancellationToken: ct);
            return;
        }

        targetUser.Role = UserRole.User;
        await db.SaveChangesAsync(ct);
        await bot.SendMessage(chatId, $"Пользователь {telegramId} понижен до обычного пользователя.", cancellationToken: ct);
    }
}
