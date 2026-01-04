using Microsoft.EntityFrameworkCore;
using PriceWatcher.Bot.Extensions;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Extensions;
using PriceWatcher.Worker.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Aspire ServiceDefaults (OpenTelemetry, Health Checks, Resilience)
builder.AddServiceDefaults();

// Database
var dbPath = builder.Configuration["DB_PATH"] ?? "pricewatcher.db";
builder.Services.AddDbContext<PriceWatcherDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContextFactory<PriceWatcherDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Product Sources
builder.Services.AddProductSources();

// Telegram Bot
var botToken = builder.Configuration["TELEGRAM_BOT_TOKEN"];
if (!string.IsNullOrEmpty(botToken))
{
    builder.Services.AddTelegramBot(builder.Configuration);
}

// Worker
builder.Services.AddPriceCheckWorker(builder.Configuration);

var app = builder.Build();

// Aspire default endpoints (/health, /alive)
app.MapDefaultEndpoints();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PriceWatcherDbContext>();
    db.Database.Migrate();
}

app.Run();
