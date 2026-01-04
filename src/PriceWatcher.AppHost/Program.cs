using Aspire.Hosting;

// Load .env file from solution root
var envPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".env");
if (File.Exists(envPath))
{
    DotNetEnv.Env.Load(envPath);
}

var builder = DistributedApplication.CreateBuilder(args);

var app = builder.AddProject("pricewatcher", "../PriceWatcher.App/PriceWatcher.App.csproj")
    .WithEnvironment("TELEGRAM_BOT_TOKEN", Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "")
    .WithEnvironment("ADMIN_TELEGRAM_IDS", Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_IDS") ?? "")
    .WithEnvironment("CHECK_CRON", Environment.GetEnvironmentVariable("CHECK_CRON") ?? "0 8,20 * * *")
    .WithEnvironment("CHECK_TIMEZONE", Environment.GetEnvironmentVariable("CHECK_TIMEZONE") ?? "Europe/Vilnius")
    .WithEnvironment("DB_PATH", "pricewatcher.db")
    .WithExternalHttpEndpoints();

builder.Build().Run();
