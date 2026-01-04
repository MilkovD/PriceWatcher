using Microsoft.EntityFrameworkCore;
using PriceWatcher.Infrastructure.Data.Entities;

namespace PriceWatcher.Infrastructure.Data;

public class PriceWatcherDbContext(DbContextOptions<PriceWatcherDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<TrackedItem> TrackedItems => Set<TrackedItem>();
    public DbSet<PriceEvent> PriceEvents => Set<PriceEvent>();
    public DbSet<BotState> BotStates => Set<BotState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TelegramUserId).IsUnique();
            entity.Property(e => e.Role).HasConversion<int>();
        });

        modelBuilder.Entity<TrackedItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserId);
            entity.Property(e => e.State).HasConversion<int>();

            entity.HasOne(e => e.User)
                .WithMany(u => u.TrackedItems)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PriceEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.TrackedItemId, e.Timestamp });
            entity.Property(e => e.Kind).HasConversion<int>();

            entity.HasOne(e => e.TrackedItem)
                .WithMany(t => t.PriceEvents)
                .HasForeignKey(e => e.TrackedItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BotState>(entity =>
        {
            entity.HasKey(e => e.TelegramUserId);
        });
    }
}
