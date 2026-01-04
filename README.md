# PriceWatcher

Telegram-бот для отслеживания цен на товары в российских интернет-магазинах.

## Возможности

- Отслеживание цен на товары Ozon (другие магазины могут быть добавлены)
- Автоматическая проверка цен по расписанию (по умолчанию 08:00 и 20:00)
- Уведомления об изменениях цен в Telegram
- История цен с графиками за 90 дней
- Rate limiting для защиты от блокировок
- OpenTelemetry метрики и трейсы

## Команды бота

- `/start` - Начать работу с ботом
- `/add <url>` - Добавить товар для отслеживания
- `/list` - Показать список отслеживаемых товаров

### Кнопки для каждого товара

- **Проверить** - Принудительно проверить цену
- **История** - Показать график цен за 90 дней
- **Удалить** - Удалить товар из отслеживания

### Админ-команды

- `/admin users` - Список пользователей
- `/admin promote <user_id>` - Сделать пользователя админом
- `/admin demote <user_id>` - Убрать права админа

## Конфигурация

Переменные окружения:

| Переменная | Описание | По умолчанию |
|------------|----------|--------------|
| `TELEGRAM_BOT_TOKEN` | Токен Telegram бота | *обязательно* |
| `ADMIN_TELEGRAM_IDS` | ID админов (через запятую) | - |
| `DB_PATH` | Путь к SQLite базе данных | `pricewatcher.db` |
| `CHECK_CRON` | Cron-выражение для проверок | `0 8,20 * * *` |
| `CHECK_TIMEZONE` | Часовой пояс для cron | `Europe/Vilnius` |
| `MAX_PARALLEL` | Макс. параллельных проверок | `5` |
| `HOST_MIN_DELAY_MS` | Мин. задержка между запросами к хосту | `2000` |
| `HOST_JITTER_MS` | Случайная добавка к задержке | `500` |
| `RETENTION_DAYS` | Срок хранения истории (дней) | `180` |
| `CLEANUP_INTERVAL_HOURS` | Интервал очистки (часов) | `24` |
| `ERROR_NOTIFICATION_COOLDOWN_HOURS` | Cooldown уведомлений об ошибках | `6` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP endpoint для телеметрии | - |

## Запуск

### Docker Compose (рекомендуется)

1. Создайте файл `.env`:

```bash
TELEGRAM_BOT_TOKEN=your_bot_token_here
ADMIN_TELEGRAM_IDS=123456789
```

2. Запустите:

```bash
docker compose up -d
```

3. Откройте Aspire Dashboard: http://localhost:18888

### Локально

1. Установите .NET 10 SDK
2. Установите Playwright браузеры:

```bash
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium
```

3. Задайте переменные окружения и запустите:

```bash
export TELEGRAM_BOT_TOKEN=your_bot_token_here
dotnet run --project src/PriceWatcher.App
```

### Devcontainer

Откройте проект в VS Code с расширением Dev Containers - окружение настроится автоматически.

## Архитектура

```
src/
├── PriceWatcher.Domain/           # Доменные интерфейсы
├── PriceWatcher.Infrastructure/   # EF Core, парсеры, источники данных
├── PriceWatcher.Bot/              # Telegram бот
├── PriceWatcher.Worker/           # Фоновые задачи (проверки, очистка)
└── PriceWatcher.App/              # Точка входа
tests/
└── PriceWatcher.Tests/            # Unit-тесты
```

## Метрики OpenTelemetry

- `pricewatcher.checks.total` - Количество проверок
- `pricewatcher.checks.failed.total` - Количество неудачных проверок
- `pricewatcher.notifications.total` - Количество отправленных уведомлений
- `pricewatcher.price_changes.total` - Количество изменений цен
- `pricewatcher.check.duration.ms` - Время выполнения проверки
- `pricewatcher.queue.size` - Размер очереди проверок

## Добавление новых источников

Для добавления нового магазина:

1. Создайте класс, реализующий `IProductSource`
2. Зарегистрируйте в `ServiceCollectionExtensions.AddProductSources()`

```csharp
public class NewStoreProductSource : IProductSource
{
    public string SourceKey => "newstore";

    public bool CanHandle(string url)
    {
        var uri = new Uri(url);
        return uri.Host.Contains("newstore.ru");
    }

    public async Task<ProductSnapshot> FetchAsync(string url, CancellationToken ct)
    {
        // Реализация парсинга
    }
}
```

## Лицензия

MIT
