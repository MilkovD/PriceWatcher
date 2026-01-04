namespace PriceWatcher.Infrastructure.Data.Entities;

public class BotState
{
    public long TelegramUserId { get; set; }
    public string State { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
