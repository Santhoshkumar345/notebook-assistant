using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotebookApi.Data;
using NotebookApi.Infrastructure;
using NotebookApi.Services;

namespace NotebookApi.Tests;

/// <summary>
/// The index is derived from the database, but a host whose disk does not last loses it while every document still says "Indexed". The reconciler
/// notices and re-sends what is missing, for each person, as that person.
/// </summary>
public sealed class ReconcilerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private StubUpstream Upstream => _factory.Upstream;
    private IndexReconciler Reconciler => _factory.Services.GetRequiredService<IndexReconciler>();

    public ReconcilerTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
    }

    /// <summary>What the AI service's index holds: each name with its chunk count. Uploads are accepted with 3 chunks.</summary>
    private void IndexHolds(string listingJson) =>
        Upstream.Respond = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/documents") => StubUpstream.Json(HttpStatusCode.OK, listingJson),
            ("POST", "/documents") => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":3}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

    private Task Store(TestUser user, string name, DocumentStatus status, int? chunks = 3, TimeSpan? age = null) =>
        _factory.DbFor(user.Id, async db =>
        {
            var when = _factory.Clock.GetUtcNow() - (age ?? TimeSpan.Zero);
            db.Documents.Add(new StoredDocument { Name = name, Content = $"# {name}", Status = status, Chunks = chunks, CreatedAt = when, UpdatedAt = when });
            await db.SaveChangesAsync();
        });

    private static string[] Posted(StubUpstream upstream) =>
        upstream.Calls.Where(c => c.Method == HttpMethod.Post).Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task A_document_the_database_calls_Indexed_but_the_index_lacks_is_sent_again()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "lost.md", DocumentStatus.Indexed);
        IndexHolds("{}"); // the index came back empty

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReconcileSummary(1, 1), summary);
        Assert.Equal(["lost.md"], Posted(Upstream));
        var send = Upstream.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal(user.Tenant, send.Headers[AiServiceCallerHandler.TenantHeader]); // as that person, not as nobody
        Assert.Equal(ApiFactory.ServiceKey, send.Headers[AiServiceCallerHandler.KeyHeader]);
        Assert.Equal("# lost.md", JsonDocument.Parse(send.Body).RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_document_the_index_already_holds_with_the_same_chunk_count_is_left_alone()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "fine.md", DocumentStatus.Indexed, chunks: 3);
        IndexHolds("""{"fine.md":3}""");

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReconcileSummary(1, 0), summary);
        Assert.Empty(Posted(Upstream));
    }

    [Fact]
    public async Task A_document_the_index_holds_with_a_different_chunk_count_is_sent_again()
    {
        // An older version of the text, or a half-finished write: the index is not what the database says it is.
        var user = await _factory.SignUpAsync();
        await Store(user, "drifted.md", DocumentStatus.Indexed, chunks: 3);
        IndexHolds("""{"drifted.md":1}""");

        await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["drifted.md"], Posted(Upstream));
    }

    [Fact]
    public async Task A_Failed_document_is_retried_and_becomes_Indexed_when_the_AI_service_accepts_it()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "failed.md", DocumentStatus.Failed, chunks: null);
        IndexHolds("{}");

        await Reconciler.RunOnceAsync(CancellationToken.None);

        var row = await _factory.DbFor(user.Id, db => db.Documents.SingleAsync());
        Assert.Equal(DocumentStatus.Indexed, row.Status);
        Assert.Equal(3, row.Chunks);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task A_Pending_document_is_left_to_whoever_is_uploading_it_until_it_has_clearly_been_abandoned()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "uploading.md", DocumentStatus.Pending, chunks: null, age: TimeSpan.FromSeconds(30));
        await Store(user, "abandoned.md", DocumentStatus.Pending, chunks: null, age: TimeSpan.FromMinutes(10));
        IndexHolds("{}");

        await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["abandoned.md"], Posted(Upstream));
    }

    [Fact]
    public async Task It_only_adds_it_never_deletes_what_the_index_holds_that_the_database_does_not_know()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "mine.md", DocumentStatus.Indexed);
        IndexHolds("""{"mine.md":3,"orphan.md":5}""");

        await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(Upstream.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Each_person_is_repaired_as_themselves_and_their_text_never_goes_out_under_anyone_elses_tenant()
    {
        var alice = await _factory.SignUpAsync();
        var bob = await _factory.SignUpAsync();
        await Store(alice, "alice.md", DocumentStatus.Indexed);
        await Store(bob, "bob.md", DocumentStatus.Indexed);
        IndexHolds("{}");

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReconcileSummary(2, 2), summary);
        var sent = Upstream.Calls.Where(c => c.Method == HttpMethod.Post)
            .Select(c => (Name: JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString(), Tenant: c.Headers[AiServiceCallerHandler.TenantHeader]))
            .ToHashSet();
        Assert.Equal(new HashSet<(string?, string)> { ("alice.md", alice.Tenant), ("bob.md", bob.Tenant) }, sent);

        // and the listing each of them was compared against was their own, not a shared one
        Assert.Equal(
            new HashSet<string> { alice.Tenant, bob.Tenant },
            Upstream.Calls.Where(c => c.Method == HttpMethod.Get).Select(c => c.Headers[AiServiceCallerHandler.TenantHeader]).ToHashSet());
    }

    [Fact]
    public async Task The_person_being_repaired_is_ambient_only_for_that_work_and_never_leaks_into_a_request_made_at_the_same_time()
    {
        var alice = await _factory.SignUpAsync();
        var bob = await _factory.SignUpAsync();
        await Store(alice, "alice.md", DocumentStatus.Indexed);

        using var aliceIsBeingRepaired = new ManualResetEventSlim();
        using var letItFinish = new ManualResetEventSlim();
        Upstream.Respond = request =>
        {
            if (request.Method == HttpMethod.Get)
                return StubUpstream.Json(HttpStatusCode.OK, "{}");
            if (request.Headers.GetValues(AiServiceCallerHandler.TenantHeader).Single() == alice.Tenant)
            {
                aliceIsBeingRepaired.Set(); // stand still in the middle of Alice's repair...
                Assert.True(letItFinish.Wait(TimeSpan.FromSeconds(10)));
            }
            return StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":3}""");
        };

        var repair = Task.Run(() => Reconciler.RunOnceAsync(CancellationToken.None));
        Assert.True(aliceIsBeingRepaired.Wait(TimeSpan.FromSeconds(10)));

        // ...while Bob uses the app normally.
        var bobUpload = await bob.Client.PostAsJsonAsync("/api/documents", new { name = "bob.md", content = "# Bob" });
        Assert.Equal(HttpStatusCode.Created, bobUpload.StatusCode);
        var bobsCall = Upstream.Calls.Single(c => c.Method == HttpMethod.Post && JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString() == "bob.md");
        Assert.Equal(bob.Tenant, bobsCall.Headers[AiServiceCallerHandler.TenantHeader]);
        Assert.Null(AmbientUser.Current); // this thread never became Alice

        letItFinish.Set();
        await repair.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(alice.Id, (await _factory.Db(db => db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Name == "alice.md"))).UserId);
        Assert.Equal(bob.Id, (await _factory.Db(db => db.Documents.IgnoreQueryFilters().SingleAsync(d => d.Name == "bob.md"))).UserId);
    }

    [Fact]
    public async Task Nobody_is_left_ambient_after_a_pass_even_when_it_fails()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "a.md", DocumentStatus.Indexed);
        Upstream.Respond = _ => throw new HttpRequestException("down");

        await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Null(AmbientUser.Current);
    }

    [Fact]
    public async Task When_the_AI_service_is_down_the_pass_stops_instead_of_trying_every_person_and_says_so()
    {
        var alice = await _factory.SignUpAsync();
        var bob = await _factory.SignUpAsync();
        await Store(alice, "a.md", DocumentStatus.Indexed);
        await Store(bob, "b.md", DocumentStatus.Indexed);
        Upstream.Respond = _ => throw new HttpRequestException("down");

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReconcileSummary(0, 0), summary); // no exception: the loop must survive to try again
        Assert.Single(Upstream.Calls); // the second person would have met the same wall
        Assert.Contains(_factory.Logs.Entries, e => e.Message.Contains("paused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task One_document_the_AI_service_refuses_does_not_stop_the_others_from_being_repaired()
    {
        var user = await _factory.SignUpAsync();
        await Store(user, "a-bad.md", DocumentStatus.Failed, chunks: null);
        await Store(user, "b-good.md", DocumentStatus.Failed, chunks: null);
        Upstream.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return StubUpstream.Json(HttpStatusCode.OK, "{}");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("a-bad.md")
                ? StubUpstream.Json(HttpStatusCode.UnprocessableEntity, """{"detail":"no"}""")
                : StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":3}""");
        };

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.DocumentsRepaired);
        var rows = await _factory.DbFor(user.Id, db => db.Documents.OrderBy(d => d.Name).ToListAsync());
        Assert.Equal([DocumentStatus.Failed, DocumentStatus.Indexed], rows.Select(r => r.Status).ToArray());
    }

    [Fact]
    public async Task With_nothing_stored_a_pass_asks_the_AI_service_nothing()
    {
        await _factory.SignUpAsync();

        var summary = await Reconciler.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new ReconcileSummary(0, 0), summary);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public void The_ambient_user_is_restored_to_what_it_was_when_a_scope_ends()
    {
        var outer = Guid.NewGuid();
        var inner = Guid.NewGuid();

        using (AmbientUser.Begin(outer))
        {
            using (AmbientUser.Begin(inner))
                Assert.Equal(inner, AmbientUser.Current);
            Assert.Equal(outer, AmbientUser.Current);
        }

        Assert.Null(AmbientUser.Current);
    }
}
