using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NotebookApi.Data;

namespace NotebookApi.Tests;

/// <summary>Checks on the database layer itself, rather than on the API built on it.</summary>
public sealed class SchemaTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly TestUser _user;

    public SchemaTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _user = factory.SignUp(); // starts the application, which applies the migrations, and gives the rows below an owner
    }

    [Fact]
    public async Task The_migrations_match_the_model_so_no_schema_change_was_forgotten()
    {
        // Fails when an entity or mapping changed without `dotnet ef migrations add`; the shipped database would then lack the change.
        Assert.False(await _factory.Db(db => Task.FromResult(db.Database.HasPendingModelChanges())));
    }

    [Fact]
    public async Task Every_migration_was_applied_at_startup()
    {
        var pending = await _factory.Db(async db => (await db.Database.GetPendingMigrationsAsync()).ToList());

        Assert.Empty(pending);
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_so_a_turn_cannot_point_at_a_missing_conversation()
    {
        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.DbFor(_user.Id, async db =>
        {
            db.Messages.Add(new ChatMessage { ConversationId = Guid.NewGuid(), Role = MessageRole.User, Text = "orphan" });
            await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task The_SQLite_library_is_a_version_without_the_known_memory_corruption_bug()
    {
        // CVE-2025-6965 (aggregate-term overflow) is fixed in SQLite 3.50.2. The NuGet package version says little about the bundled engine, so ask the engine.
        var version = await _factory.Db(async db =>
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "select sqlite_version()";
            return Version.Parse((string)(await command.ExecuteScalarAsync())!);
        });

        Assert.True(version >= new Version(3, 50, 2), $"SQLite {version} is older than 3.50.2");
    }

    [Fact]
    public async Task Timestamps_round_trip_as_the_same_instant_and_sort_chronologically()
    {
        var earlier = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var later = earlier.AddDays(40);
        await _factory.DbFor(_user.Id, async db =>
        {
            db.Conversations.AddRange(
                new Conversation { Id = Guid.NewGuid(), Title = "later", CreatedAt = later, UpdatedAt = later },
                new Conversation { Id = Guid.NewGuid(), Title = "earlier", CreatedAt = earlier, UpdatedAt = earlier });
            await db.SaveChangesAsync();
        });

        var titles = await _factory.DbFor(_user.Id, db => db.Conversations.OrderBy(c => c.UpdatedAt).Select(c => c.Title).ToListAsync());
        var read = await _factory.DbFor(_user.Id, db => db.Conversations.Where(c => c.Title == "earlier").Select(c => c.CreatedAt).SingleAsync());

        Assert.Equal(["earlier", "later"], titles);
        Assert.Equal(earlier, read);
    }
}
