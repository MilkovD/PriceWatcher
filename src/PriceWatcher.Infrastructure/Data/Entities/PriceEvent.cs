namespace PriceWatcher.Infrastructure.Data.Entities;

public enum PriceEventKind
{
    Change = 0,
    Snapshot = 1,
    Missing = 2,
    Recovered = 3,
    Failed = 4
}

public class PriceEvent
{
    public int Id { get; set; }
    public int TrackedItemId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public PriceEventKind Kind { get; set; }
    public long? PriceMinor { get; set; }
    public string? RawText { get; set; }
    public string? Note { get; set; }

    public TrackedItem TrackedItem { get; set; } = null!;
}
