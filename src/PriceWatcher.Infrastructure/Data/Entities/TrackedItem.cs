namespace PriceWatcher.Infrastructure.Data.Entities;

public enum ItemState
{
    Ok = 0,
    PriceMissing = 1,
    Failed = 2
}

public class TrackedItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Url { get; set; } = string.Empty;
    public string SourceKey { get; set; } = string.Empty;
    public string? SourceConfigJson { get; set; }
    public string Title { get; set; } = string.Empty;
    public ItemState State { get; set; } = ItemState.Ok;
    public long? LastKnownPriceMinor { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public string? LastError { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset? LastErrorNotifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<PriceEvent> PriceEvents { get; set; } = new List<PriceEvent>();
}
