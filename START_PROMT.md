Ты — senior .NET архитектор и тимлид. Нужно создать проект **с нуля**: сервис отслеживания цен товаров. Управление через **Telegram-бота**. На старте поддерживаем **только Ozon (ozon.ru)**. Пользователь для Ozon (и в будущем для любых маркетплейсов) передаёт **только URL товара**. Название товара и цена извлекаются автоматически со страницы. Способ парсинга фиксирован для каждого маркетплейса (один провайдер = один способ/набор fallback-стратегий). Архитектуру минимально подготовить для добавления других маркетплейсов и на ближайшее будущее учесть возможность отслеживания **произвольных сайтов** (но в MVP этот режим можно не включать).

### 0) Главные требования (MVP)

1. **Добавление товара**

* Пользователь в боте вводит **URL товара** (пока только ozon.ru).
* Сервис автоматически определяет `SourceKey` (например `ozon`) по URL.
* Выполняется **test-fetch**:

  * извлечь `Title` (имя товара) и `Price` (цена)
  * если цену извлечь не удалось → товар всё равно можно сохранить, но со статусом “цена не найдена/товар недоступен”.
* Никаких XPath/CSS селекторов от пользователя в MVP.

2. **Плановые проверки**

* Проверять цены **2 раза в сутки** для всех товаров.
* Должна быть **очередь заданий** и ограничения:

  * глобальный лимит параллельности (конфиг, по умолчанию 5–10)
  * лимит по хосту: **не более 1 одновременного запроса на домен** + минимальная задержка между запросами к одному домену (конфиг) + небольшой jitter.

3. **История и уведомления**

* Храним историю **6 месяцев**.
* Храним **изменения** + **редкие снапшоты**:

  * Change — только когда цена изменилась
  * Snapshot — 1 раз в сутки (первый успешный чек дня) для построения графиков и “ровной” истории
  * Missing — когда цена пропала/не найдена
  * Recovered — когда цена вернулась после Missing
  * Failed — когда чек упал из-за сетевых ошибок/парсинга
* Уведомлять пользователя:

  * на Change (любое изменение цены)
  * на Missing/Recovered
  * на Failed, но с анти-спамом: не чаще 1 раза в X часов на один и тот же item с той же причиной.

4. **Команды Telegram-бота**

* `/start` — регистрация пользователя, показать меню.
* `/add` — добавить товар по URL.
* `/list` — список товаров пользователя (кнопками).
* Карточка товара:

  * “Проверить сейчас”
  * “История (график)”
  * “Удалить”
* Админ-команды:

  * `/admin users` — список пользователей + сколько товаров
  * `/admin promote <telegramId>`
  * `/admin demote <telegramId>`
* Роли: `User` и `Admin`. Начальные админы задаются env `ADMIN_TELEGRAM_IDS`.

5. **История с графиком**

* По запросу истории бот присылает:

  * краткие агрегаты за период (по умолчанию 90 дней): min/max/avg/last + изменение от первой точки к последней (абсолютное и %)
  * **PNG-график** цены от времени (X=дата, Y=цена) и отправляет как photo.

### 1) Технологии и ограничения

* .NET: **.NET 10** (если в окружении недоступно — .NET 9, но старайся под 10).
* База: **SQLite + EF Core** (миграции обязательно).
* Telegram: `Telegram.Bot`.
* Ozon парсинг: **Playwright** (headless Chromium). Считай Ozon динамическим сайтом.
* График: ScottPlot (предпочтительно) → PNG.
* Observability: OpenTelemetry (traces/metrics/logs) + экспорт OTLP; визуализация через **Aspire Dashboard**.
* Docker: приложение — **один контейнер**. Отдельно в compose можно поднимать Aspire Dashboard (и при необходимости OTEL Collector).
* Devcontainers: добавить `.devcontainer/` для разработки.
* Стиль: максимально простой код, без излишней абстракции. Но архитектурные границы соблюсти.

### 2) Архитектура (обязательная, но минимальная)

Нужна плагинная схема источников:

**Domain**:

* `ProductSnapshot`:

  * `CanonicalUrl` (string)
  * `Title` (string)
  * `PriceMinor` (long? nullable, цена в копейках)
  * `Currency` (string, пока “RUB”)
  * `Availability` (enum InStock/OutOfStock/Unknown)
  * `CapturedAt` (DateTimeOffset)
* `IProductSource`:

  * `string SourceKey { get; }` (например `"ozon"`)
  * `bool CanHandle(string url)`
  * `Task<ProductSnapshot> FetchAsync(string url, CancellationToken ct)`
* `IProductSourceResolver`:

  * `IProductSource Resolve(string url)` (или TryResolve)
  * нормализация URL (убрать UTM/лишние параметры если безопасно; минимум — trim, decode, убрать #)

**Infrastructure**:

* `OzonProductSource : IProductSource` (реализация сейчас)
* (заготовка) `CustomProductSource : IProductSource` — может быть заглушкой (не включать в UX), но предусмотреть, что в будущем у него будет конфиг (селекторы/правила), поэтому в БД надо поле `SourceConfigJson`.

**App**:

* один entrypoint, который поднимает:

  * Minimal API (хотя бы `/health`)
  * Telegram bot hosted service
  * Worker hosted service (планировщик + очередь)

Разнеси по проектам, но executable один:

* `PriceWatcher.App` (exe)
* `PriceWatcher.Domain`
* `PriceWatcher.Infrastructure`
* `PriceWatcher.Bot`
* `PriceWatcher.Worker`

### 3) Модель данных (SQLite/EF Core)

Таблицы:

**Users**

* `Id` (int PK)
* `TelegramUserId` (long unique)
* `Role` (int enum)
* `CreatedAt`

**TrackedItems**

* `Id` (int PK)
* `UserId` (FK)
* `Url` (string)
* `SourceKey` (string) — например “ozon”
* `SourceConfigJson` (string? nullable) — задел на будущее
* `Title` (string) — автозаполняется, обновляется при fetch (если изменилось)
* `State` (int enum: Ok/PriceMissing/Failed)
* `LastKnownPriceMinor` (long? nullable)
* `LastCheckAt` (DateTimeOffset? nullable)
* `LastError` (string? nullable)
* `LastErrorCode` (string? nullable) — для анти-спама по причине
* `LastErrorNotifiedAt` (DateTimeOffset? nullable)
* `CreatedAt`, `UpdatedAt`

**PriceEvents**

* `Id` (int PK)
* `TrackedItemId` (FK)
* `Timestamp` (DateTimeOffset)
* `Kind` (int enum: Change/Snapshot/Missing/Recovered/Failed)
* `PriceMinor` (long? nullable)
* `RawText` (string? nullable) — для дебага
* `Note` (string? nullable)

**BotStates** (для wizard / меню)

* `TelegramUserId` (long PK)
* `State` (string)
* `PayloadJson` (string)
* `UpdatedAt`

Индексы:

* Users.TelegramUserId unique
* TrackedItems.UserId
* PriceEvents.(TrackedItemId, Timestamp)

Миграции EF Core обязаны быть в репе.

Retention job: удалять PriceEvents старше 6 месяцев (конфигurable).

### 4) Ozon парсинг (внутри OzonProductSource)

Playwright:

* выставить user-agent (обычный десктопный), язык `ru-RU`, timezone `Europe/Vilnius` (или configurable).
* `Goto(url)` + дождаться `DOMContentLoaded`, затем небольшой `WaitForTimeout` (например 500–1500ms).
* Извлечение **Title**:

  * приоритет: `meta[property="og:title"]`
  * fallback: `document.title`
* Извлечение **Price**:

  * реализуй несколько стратегий, но всё внутри провайдера:

    1. попытка извлечь цену из встроенного JSON/state (если есть в HTML/скриптах)
    2. попытка из структурированных данных (JSON-LD, meta)
    3. fallback DOM (если удастся)
* Если цену не нашли/не распарсили → `PriceMinor = null`, `Availability = Unknown/OutOfStock` (как разумно определить), состояние item → PriceMissing.
* Парсинг цены:

  * поддержи строки типа “1 234 ₽”, “1 234,56”, “3456 р/шт”.
  * хранить в **копейках** `long` (minor units).
  * тесты на парсер (юнит-тесты): минимум 10 кейсов.

Важно: если Ozon иногда блокирует headless, MVP должен корректно фиксировать Failed/Missing и уведомлять пользователя, плюс логировать причину. Не пытайся строить “обходы” сложных антиботов в MVP.

### 5) Очередь и планировщик

* Планировщик: cron-выражение из env `CHECK_CRON` (по умолчанию два раза в сутки, например 08:00 и 20:00 по Europe/Vilnius).
* Очередь: `Channel<int>` (itemId).
* Выполнение:

  * глобальный `SemaphoreSlim(MAX_PARALLEL)`
  * `HostRateLimiter`:

    * на host хранить `SemaphoreSlim(1)` + `lastRequestAt`
    * перед запросом выдерживать `HOST_MIN_DELAY_MS` с jitter.
* “Проверить сейчас” в боте:

  * поставить item в очередь немедленно (или выполнить напрямую, но всё равно через rate limiter).

### 6) Логика событий и уведомлений

При результате fetch:

* Если `PriceMinor == null`:

  * если раньше была цена → событие Missing + уведомление.
  * если и раньше не было цены → не спамить, но обновить `LastCheckAt`, `State`.
* Если `PriceMinor != null`:

  * если раньше не было цены → событие Recovered + уведомление.
  * если цена изменилась → событие Change + уведомление (с old/new).
  * если цена не изменилась → без события (кроме Snapshot).
* Snapshot:

  * 1 раз в сутки при первом успешном чеке товара (цена != null): событие Snapshot.
* Ошибки:

  * если fetch/parse упал → событие Failed, состояние Failed.
  * уведомлять об ошибке с анти-спамом по `LastErrorCode` + `LastErrorNotifiedAt`.

### 7) История и график (Telegram)

* Команда/кнопка “История”:

  * период по умолчанию 90 дней; кнопки 30/90/180.
  * собрать точки из PriceEvents видов Change и Snapshot.
  * построить график ScottPlot:

    * X: DateTime
    * Y: PriceMinor / 100.0
    * подписи осей: “Дата”, “Цена”
    * если мало точек — всё равно строить (маркер).
  * агрегаты: min/max/avg/last, изменение (abs и %).
  * отправить PNG как photo + подпись текстом.

### 8) Observability и Aspire Dashboard (важно)

Нужно включить OpenTelemetry:

* Traces:

  * ASP.NET Core
  * HttpClient
  * кастомный span “CheckItem” с атрибутами (sourceKey, host, result).
* Metrics:

  * counters: checks_total, checks_failed_total, notifications_total
  * histogram: check_duration_ms, fetch_duration_ms
  * gauge/observable: queue_length (примерно)
* Logs:

  * структурированные логи в stdout (JSON желательно)
  * OTEL logs exporter по возможности
* Экспорт:

  * `OTEL_EXPORTER_OTLP_ENDPOINT` configurable.
* docker-compose:

  * `app` (наш контейнер)
  * `aspire-dashboard` (официальный образ)
  * если понадобится для совместимости — `otel-collector`, но не усложняй без необходимости.
* README: как открыть dashboard и что там видно.

### 9) Docker, Devcontainer, репозиторий

В репе должны быть:

* `.editorconfig`
* `Directory.Packages.props` (Central Package Mgmt)
* `Directory.Build.props` (nullable, warnings, langver)
* `.devcontainer/` (для разработки)
* `Dockerfile` multi-stage
* `docker-compose.yml` (app + aspire dashboard)
* `README.md` с инструкцией запуска:

  * env vars: TELEGRAM_BOT_TOKEN, DB_PATH, ADMIN_TELEGRAM_IDS, CHECK_CRON, MAX_PARALLEL, HOST_MIN_DELAY_MS, OTEL_EXPORTER_OTLP_ENDPOINT
  * как применяются миграции (на старте автоматически)
  * как пользоваться ботом

### 10) Ограничения на “не переусложнять”

* Не делай микросервисы.
* Не добавляй брокеры/Redis в MVP.
* Не делай сложный CQRS/DDD фреймворки — только чистые интерфейсы и простые сервисы.
* DI стандартный ASP.NET Core.
* Любые “умные” решения — только если они реально нужны требованиям.

---

## План выполнения (делай поэтапно, каждый этап = рабочее состояние)

Действуй итеративно, каждый этап должен:

* собираться `dotnet build`
* запускаться локально
* иметь понятный минимальный функционал

Этапы:

1. Инициализация репо + solution + проекты + общие props/CPM.
2. EF Core SQLite модели + миграции + автоприменение миграций при старте.
3. Hosted Telegram bot: `/start`, `/add` (только URL, пока без реального fetch можно stub) + `/list`.
4. Реальный `OzonProductSource` через Playwright + парсер цены + unit tests.
5. Интеграция: `/add` делает test-fetch и сохраняет Title/Price.
6. Worker: cron 2 раза/сутки + очередь + host rate limit + уведомления.
7. История и PNG-график ScottPlot.
8. Retention 6 месяцев + Snapshot 1 раз/сутки.
9. OTEL instrumentation + docker-compose с Aspire Dashboard.
10. Dockerfile + Devcontainer + README.

---

## Acceptance Criteria (что считается “готово”)

* Пользователь может добавить ozon URL и увидеть сохранённый товар с автозагруженным названием.
* Плановая проверка 2 раза/сутки работает и не долбит ozon (host limiter).
* При изменении цены приходят уведомления.
* При запросе истории бот присылает PNG график и агрегаты.
* EF Core SQLite используется, миграции в репе, файл БД в volume.
* В docker-compose поднимается Aspire Dashboard и видно метрики/логи/трейсы (минимально).
* Код чистый, без лишних зависимостей.

---

## Важные подсказки по пакетам (можешь выбрать близкие аналоги)

* EF Core SQLite: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`
* Telegram: `Telegram.Bot`
* Playwright: `Microsoft.Playwright`
* Графики: `ScottPlot` (современная версия)
* OTEL: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`
* Логи: либо чистый ILogger + JSON console, либо Serilog (если проще структурировать)

---

Начинай выполнять сейчас: создай репозиторий, solution и проекты, подключи Central Package Management, добавь базовые файлы (`.editorconfig`, `Directory.Build.props`, `Directory.Packages.props`), затем переходи к EF Core и миграциям.

---

Если хочешь, я могу следующим сообщением дать ещё и “микро-промпты” для Claude Code по каждому этапу (1–10) так, чтобы ты просто запускал их по очереди, получая маленькие коммиты.
