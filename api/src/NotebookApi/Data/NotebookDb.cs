using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace NotebookApi.Data;

public sealed class NotebookDb(DbContextOptions<NotebookDb> options) : DbContext(options)
{
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration) =>
        // The SQLite provider cannot ORDER BY a DateTimeOffset stored as text. The binary form sorts correctly, and every
        // value written here is UTC (from TimeProvider), so the offsets are equal.
        configuration.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StoredDocument>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.Property(d => d.Name).HasMaxLength(100);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Error).HasMaxLength(32);
        });

        model.Entity<Conversation>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(Services.ConversationService.MaxTitleChars);
            e.HasIndex(c => c.UpdatedAt);
            e.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.Status).HasMaxLength(16);
            e.Property(m => m.Action).HasMaxLength(16);
            e.Property(m => m.RequestId).HasMaxLength(64);
        });
    }
}
