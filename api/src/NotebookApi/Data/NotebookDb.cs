using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NotebookApi.Infrastructure;

namespace NotebookApi.Data;

/// <summary>
/// Every document, conversation and message belongs to one user, and this context only ever sees the signed-in user's rows:
/// a global query filter on each owned type, and a check at save time that nothing is written for anyone else. When nobody is signed
/// in the filter matches nothing, so a missing user fails closed instead of showing everything.
/// </summary>
public sealed class NotebookDb(DbContextOptions<NotebookDb> options, ICurrentUser currentUser) : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<StoredDocument> Documents => Set<StoredDocument>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    /// <summary>Read by the query filters below. Guid.Empty (no user) matches no row.</summary>
    private Guid CurrentUserId => currentUser.Id ?? Guid.Empty;

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration) =>
        // The SQLite provider cannot ORDER BY a DateTimeOffset stored as text. The binary form sorts correctly, and every
        // value written here is UTC (from TimeProvider), so the offsets are equal.
        configuration.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model); // the Identity tables

        model.Entity<StoredDocument>(e =>
        {
            e.HasIndex(d => new { d.UserId, d.Name }).IsUnique(); // a name is unique per user: two people can each have notes.md
            e.Property(d => d.Name).HasMaxLength(100);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Error).HasMaxLength(32);
            e.HasQueryFilter(d => d.UserId == CurrentUserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Conversation>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(Services.ConversationService.MaxTitleChars);
            e.HasIndex(c => new { c.UserId, c.UpdatedAt });
            e.HasQueryFilter(c => c.UserId == CurrentUserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.Status).HasMaxLength(16);
            e.Property(m => m.Action).HasMaxLength(16);
            e.Property(m => m.RequestId).HasMaxLength(64);
            // Messages carry the owner themselves, so a query that goes straight to messages is filtered too, not only one that goes through a conversation.
            e.HasQueryFilter(m => m.UserId == CurrentUserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceOwnership();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceOwnership();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// New rows are stamped with the signed-in user; a row that already names someone else, or any attempt to move a row to another user,
    /// is refused. With nobody signed in nothing can be written. This is the write-side twin of the query filters.
    /// </summary>
    private void EnforceOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<IOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (currentUser.Id is not { } user)
                    throw new InvalidOperationException("Nothing can be saved without a signed-in user.");
                if (entry.Entity.UserId == Guid.Empty)
                    entry.Entity.UserId = user;
                else if (entry.Entity.UserId != user)
                    throw new InvalidOperationException("A row cannot be created for another user.");
            }
            else if (entry.State == EntityState.Modified && entry.Property(nameof(IOwned.UserId)).IsModified)
            {
                throw new InvalidOperationException("A row cannot be moved to another user.");
            }
        }
    }
}
