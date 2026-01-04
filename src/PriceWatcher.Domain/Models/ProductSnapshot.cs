namespace PriceWatcher.Domain.Models;

public enum Availability
{
    Unknown = 0,
    InStock = 1,
    OutOfStock = 2
}

public record ProductSnapshot(
    string CanonicalUrl,
    string Title,
    long? PriceMinor,
    string Currency,
    Availability Availability,
    DateTimeOffset CapturedAt);
