using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PriceWatcher.Bot.Extensions;
using PriceWatcher.Infrastructure.Data;
using PriceWatcher.Infrastructure.Extensions;
using PriceWatcher.Worker.Extensions;
using PriceWatcher.Worker.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(
            serviceName: PriceWatcherMetrics.ServiceName,
            serviceVersion: PriceWatcherMetrics.ServiceVersion))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(PriceWatcherMetrics.ServiceName);

        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            tracing.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
            });
        }
        else
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(PriceWatcherMetrics.ServiceName);

        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            metrics.AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(otlpEndpoint);
            });
        }
        else
        {
            metrics.AddConsoleExporter();
        }
    });

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

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PriceWatcherDbContext>();
    db.Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.Run();
