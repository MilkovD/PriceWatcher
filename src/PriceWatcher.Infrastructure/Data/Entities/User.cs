namespace PriceWatcher.Infrastructure.Data.Entities;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class User
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<TrackedItem> TrackedItems { get; set; } = new List<TrackedItem>();
}
