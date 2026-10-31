using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NotebookApi.Contracts;
using NotebookApi.Data;

namespace NotebookApi.Tests;

/// <summary>
/// Two people use the same API and must never see, change or delete each other's documents and conversations, whatever they send. Checked at
/// every layer: through the HTTP API, in what the AI service is told, in the database's own query filters and in what it refuses to save.
/// </summary>
public sealed class IsolationTests : IClassFixture<ApiFactory>
{
    private const string Answered = """{"status":"answered","answer":"x","sources":[],"action":"retrieve","trace":[]}""";

    private readonly ApiFactory _factory;
    private readonly TestUser _alice;
    private readonly TestUser _bob;
    private StubUpstream Upstream => _factory.Upstream;

    public IsolationTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _alice = factory.SignUp();
        _bob = factory.SignUp();
        Upstream.Respond = request => request.Method.Method switch
        {
            "POST" when request.RequestUri!.AbsolutePath == "/ask" => StubUpstream.Json(HttpStatusCode.OK, Answered),
            "POST" => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":2}"""),
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => StubUpstream.Json(HttpStatusCode.OK, "{}"),
        };
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> Upload(TestUser who, string name, string content) =>
        who.Client.PostAsJsonAsync("/api/documents", new { name, content });

    private static async Task<string[]> DocumentNames(TestUser who) =>
        (await Json(await who.Client.GetAsync("/api/documents"))).EnumerateArray().Select(d => d.GetProperty("name").GetString()!).ToArray();

    private static async Task<Guid> Ask(TestUser who, string question, Guid? conversationId = null, bool answersClarification = false)
    {
        var response = await who.Client.PostAsJsonAsync("/api/ask", new { question, conversationId, answersClarification });
        response.EnsureSuccessStatusCode();
        return (await Json(response)).GetProperty("conversationId").GetGuid();
    }

    // ----------------------------------------------------------------- documents, over HTTP

    [Fact]
    public async Task Two_people_can_each_have_a_document_with_the_same_name_and_see_only_their_own()
    {
        Assert.Equal(HttpStatusCode.Created, (await Upload(_alice, "notes.md", "# Alice's notes")).StatusCode);

        Assert.Empty(await DocumentNames(_bob)); // nothing of Alice's shows

        Assert.Equal(HttpStatusCode.Created, (await Upload(_bob, "notes.md", "# Bob's notes")).StatusCode); // created, not "replaced": it is Bob's own
        Assert.Equal(["notes.md"], await DocumentNames(_alice));
        Assert.Equal(["notes.md"], await DocumentNames(_bob));
        Assert.Equal("# Alice's notes", (await _factory.DbFor(_alice.Id, db => db.Documents.SingleAsync())).Content);
        Assert.Equal("# Bob's notes", (await _factory.DbFor(_bob.Id, db => db.Documents.SingleAsync())).Content);
    }

    [Fact]
    public async Task One_person_cannot_delete_or_reindex_anothers_document()
    {
        await Upload(_alice, "notes.md", "# Alice's notes");
        Upstream.Reset();

        Assert.Equal(HttpStatusCode.NotFound, (await _bob.Client.DeleteAsync("/api/documents/notes.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.Client.PostAsync("/api/documents/notes.md/reindex", null)).StatusCode);

        Assert.Empty(Upstream.Calls); // refused before the AI service was asked to do anything
        Assert.Equal(["notes.md"], await DocumentNames(_alice));
    }

    [Fact]
    public async Task Syncing_only_touches_the_callers_own_index()
    {
        await Upload(_alice, "alice.md", "# A");
        await Upload(_bob, "bob.md", "# B");
        Upstream.Reset();
        Upstream.Respond = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/documents") => StubUpstream.Json(HttpStatusCode.OK, """{"bob.md":2,"stray.md":1}"""),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":2}"""),
        };

        var result = await Json(await _bob.Client.PostAsync("/api/documents/sync", null));

        Assert.Equal(1, result.GetProperty("reindexed").GetInt32()); // Bob's one document, not Alice's
        Assert.Equal(["stray.md"], result.GetProperty("removedFromIndex").EnumerateArray().Select(n => n.GetString()).ToArray());
        Assert.All(Upstream.Calls, call => Assert.Equal(_bob.Tenant, call.Headers["X-Tenant-ID"])); // every call was made as Bob
    }

    [Fact]
    public async Task One_persons_slow_upload_does_not_make_another_person_wait()
    {
        using var release = new ManualResetEventSlim();
        var aliceStarted = new TaskCompletionSource();
        Upstream.Respond = request =>
        {
            if (request.Headers.GetValues("X-Tenant-ID").Single() == _alice.Tenant)
            {
                aliceStarted.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(15)); // Alice's document is still being indexed
            }

            return StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":1}""");
        };

        var alice = Upload(_alice, "alice.md", "# A");
        await aliceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); // a timeout, so a broken upload fails this test instead of hanging the run

        var bob = await Upload(_bob, "bob.md", "# B").WaitAsync(TimeSpan.FromSeconds(10)); // would time out if one gate served everyone

        Assert.Equal(HttpStatusCode.Created, bob.StatusCode);
        release.Set();
        Assert.Equal(HttpStatusCode.Created, (await alice).StatusCode);
    }

    // ----------------------------------------------------------------- what the AI service is told

    [Fact]
    public async Task Every_call_to_the_AI_service_carries_the_service_key_and_the_callers_own_tenant()
    {
        await Upload(_alice, "notes.md", "# A");
        await _alice.Client.PostAsJsonAsync("/api/ask", new { question = "Which store is used?" });
        await _bob.Client.GetAsync("/api/documents"); // a database-only call: nothing is sent upstream
        await Upload(_bob, "notes.md", "# B");

        var calls = Upstream.Calls;
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call => Assert.Equal(ApiFactory.ServiceKey, call.Headers["X-Service-Key"]));
        Assert.Equal([_alice.Tenant, _alice.Tenant, _bob.Tenant], calls.Select(c => c.Headers["X-Tenant-ID"]).ToArray());
        Assert.Matches("^[0-9a-f]{32}$", _alice.Tenant);
    }

    [Fact]
    public async Task A_tenant_header_sent_by_the_browser_is_never_passed_on()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent.Create(new { name = "notes.md", content = "# A" }) };
        request.Headers.Add("X-Tenant-ID", _bob.Tenant); // Alice tries to act as Bob
        request.Headers.Add("X-Service-Key", "something-she-made-up-0123456789");

        await _alice.Client.SendAsync(request);

        var call = Assert.Single(Upstream.Calls);
        Assert.Equal(_alice.Tenant, call.Headers["X-Tenant-ID"]);
        Assert.Equal(ApiFactory.ServiceKey, call.Headers["X-Service-Key"]);
    }

    [Fact]
    public async Task The_anonymous_health_check_sends_the_key_but_no_tenant()
    {
        var anonymous = await _factory.AnonymousAsync();
        Upstream.Reset();

        await anonymous.GetAsync("/api/health");

        var call = Assert.Single(Upstream.Calls);
        Assert.Equal(ApiFactory.ServiceKey, call.Headers["X-Service-Key"]);
        Assert.False(call.Headers.ContainsKey("X-Tenant-ID"));
    }

    // ----------------------------------------------------------------- conversations, over HTTP

    [Fact]
    public async Task One_person_cannot_see_open_or_delete_anothers_conversation()
    {
        var aliceConversation = await Ask(_alice, "Alice's private question");

        var bobList = await Json(await _bob.Client.GetAsync("/api/conversations"));
        Assert.Equal(0, bobList.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.Client.GetAsync($"/api/conversations/{aliceConversation}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _bob.Client.DeleteAsync($"/api/conversations/{aliceConversation}")).StatusCode);

        var aliceView = await Json(await _alice.Client.GetAsync($"/api/conversations/{aliceConversation}"));
        Assert.Equal(2, aliceView.GetProperty("messages").GetArrayLength()); // untouched
    }

    [Fact]
    public async Task One_person_cannot_add_to_anothers_conversation_and_no_model_call_is_spent_trying()
    {
        var aliceConversation = await Ask(_alice, "Alice's private question");
        Upstream.Reset();

        var response = await _bob.Client.PostAsJsonAsync("/api/ask", new { question = "Bob writes into it", conversationId = aliceConversation });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Upstream.Calls);
        Assert.Equal(2, await _factory.DbFor(_alice.Id, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task One_person_cannot_answer_a_clarifying_question_that_was_put_to_someone_else()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"status":"clarify","answer":"Which one?","sources":[],"action":"clarify","trace":[]}""");
        var aliceConversation = await Ask(_alice, "What was the limit?");
        Upstream.Reset();

        var response = await _bob.Client.PostAsJsonAsync("/api/ask", new { question = "the embedding limit", conversationId = aliceConversation, answersClarification = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task Each_person_sees_only_their_own_conversations_in_the_list()
    {
        await Ask(_alice, "Alice's question");
        await Ask(_bob, "Bob's question");
        await Ask(_bob, "Bob's other question");

        var alices = await Json(await _alice.Client.GetAsync("/api/conversations"));
        var bobs = await Json(await _bob.Client.GetAsync("/api/conversations"));

        Assert.Equal(["Alice's question"], alices.EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
        Assert.Equal(2, bobs.GetArrayLength());
    }

    // ----------------------------------------------------------------- the database's own guarantees

    [Fact]
    public async Task The_database_shows_a_user_none_of_anothers_rows_even_when_asked_for_everything()
    {
        await Upload(_alice, "alice.md", "# A");
        await Ask(_alice, "Alice's question");

        // Straight to each table, the way a query that forgot its Where would go.
        Assert.Equal(0, await _factory.DbFor(_bob.Id, db => db.Documents.CountAsync()));
        Assert.Equal(0, await _factory.DbFor(_bob.Id, db => db.Conversations.CountAsync()));
        Assert.Equal(0, await _factory.DbFor(_bob.Id, db => db.Messages.CountAsync())); // messages are filtered on their own, not only through a conversation
        Assert.Equal(0, await _factory.DbFor(null, db => db.Documents.CountAsync()));    // nobody signed in sees nothing, not everything
        Assert.Equal(0, await _factory.DbFor(null, db => db.Messages.CountAsync()));

        Assert.Equal(1, await _factory.DbFor(_alice.Id, db => db.Documents.CountAsync()));
        Assert.Equal(2, await _factory.DbFor(_alice.Id, db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task A_bulk_delete_by_one_user_cannot_reach_anothers_rows()
    {
        await Upload(_alice, "alice.md", "# A");
        await Ask(_alice, "Alice's question");

        await _factory.DbFor(_bob.Id, async db =>
        {
            await db.Documents.ExecuteDeleteAsync(); // "delete everything", as a careless bulk operation would
            await db.Messages.ExecuteDeleteAsync();
            await db.Conversations.ExecuteDeleteAsync();
        });

        Assert.Equal(1, await _factory.DbFor(_alice.Id, db => db.Documents.CountAsync()));
        Assert.Equal(2, await _factory.DbFor(_alice.Id, db => db.Messages.CountAsync()));
        Assert.Equal(1, await _factory.DbFor(_alice.Id, db => db.Conversations.CountAsync()));
    }

    [Fact]
    public async Task Every_type_that_belongs_to_a_user_has_a_query_filter_so_a_new_one_cannot_be_forgotten()
    {
        var owned = await _factory.Db(db => Task.FromResult(db.Model.GetEntityTypes().Where(t => typeof(IOwned).IsAssignableFrom(t.ClrType)).ToList()));

        Assert.True(owned.Count >= 3, "expected at least documents, conversations and messages to be owned");
        Assert.All(owned, type => Assert.True(type.GetQueryFilter() is not null, $"{type.ClrType.Name} is owned by a user but has no query filter"));
    }

    [Fact]
    public async Task New_rows_are_stamped_with_the_signed_in_user()
    {
        await _factory.DbFor(_alice.Id, async db =>
        {
            db.Documents.Add(new StoredDocument { Name = "a.md", Content = "# A" });
            await db.SaveChangesAsync();
        });

        Assert.Equal(_alice.Id, (await _factory.DbFor(_alice.Id, db => db.Documents.SingleAsync())).UserId);
    }

    [Fact]
    public async Task A_row_cannot_be_created_for_another_user()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _factory.DbFor(_alice.Id, async db =>
        {
            db.Documents.Add(new StoredDocument { Name = "a.md", Content = "# A", UserId = _bob.Id });
            await db.SaveChangesAsync();
        }));
        Assert.Equal(0, await _factory.DbFor(_bob.Id, db => db.Documents.CountAsync()));
    }

    [Fact]
    public async Task Nothing_can_be_saved_without_a_signed_in_user()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _factory.DbFor(null, async db =>
        {
            db.Documents.Add(new StoredDocument { Name = "a.md", Content = "# A" });
            await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task A_row_cannot_be_handed_to_another_user()
    {
        await Upload(_alice, "alice.md", "# A");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _factory.DbFor(_alice.Id, async db =>
        {
            var document = await db.Documents.SingleAsync();
            document.UserId = _bob.Id;
            await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task A_document_name_is_unique_per_user_in_the_database_itself()
    {
        await Upload(_alice, "notes.md", "# A");
        await Upload(_bob, "notes.md", "# B"); // another user's identical name is fine

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.DbFor(_alice.Id, async db =>
        {
            db.Documents.Add(new StoredDocument { Name = "notes.md", Content = "duplicate" });
            await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Deleting_an_account_deletes_everything_it_owned_and_nothing_else()
    {
        await Upload(_alice, "alice.md", "# A");
        await Ask(_alice, "Alice's question");
        await Upload(_bob, "bob.md", "# B");

        await _factory.Db(async db => { db.Users.Remove(db.Users.Single(u => u.Id == _alice.Id)); await db.SaveChangesAsync(); });

        Assert.Equal(0, await _factory.Db(db => db.Documents.IgnoreQueryFilters().CountAsync(d => d.UserId == _alice.Id)));
        Assert.Equal(0, await _factory.Db(db => db.Conversations.IgnoreQueryFilters().CountAsync(c => c.UserId == _alice.Id)));
        Assert.Equal(0, await _factory.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.UserId == _alice.Id)));
        Assert.Equal(1, await _factory.DbFor(_bob.Id, db => db.Documents.CountAsync()));
    }
}
