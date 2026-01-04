using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var app = builder.AddProject("pricewatcher", "../PriceWatcher.App/PriceWatcher.App.csproj")
    .WithEnvironment("TELEGRAM_BOT_TOKEN", builder.Configuration["TELEGRAM_BOT_TOKEN"] ?? "")
    .WithEnvironment("ADMIN_TELEGRAM_IDS", builder.Configuration["ADMIN_TELEGRAM_IDS"] ?? "")
    .WithEnvironment("CHECK_CRON", builder.Configuration["CHECK_CRON"] ?? "0 8,20 * * *")
    .WithEnvironment("CHECK_TIMEZONE", builder.Configuration["CHECK_TIMEZONE"] ?? "Europe/Vilnius")
    .WithEnvironment("DB_PATH", "/data/pricewatcher.db")
    .WithExternalHttpEndpoints();

builder.Build().Run();
