using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace NotebookApi.Tests;

/// <summary>What one person may store. Limits are lowered so the tests need a handful of rows, not fifty.</summary>
public sealed class QuotaTests : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;
    private readonly WebApplicationFactory<Program> _limited;
    private StubUpstream Upstream => _factory.Upstream;

    public QuotaTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _limited = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Quotas:MaxDocuments", "2");
            b.UseSetting("Quotas:MaxTotalDocumentChars", "100");
            b.UseSetting("Quotas:MaxConversations", "2");
            b.UseSetting("Quotas:MaxMessagesPerConversation", "4");
        });
        Healthy();
    }

    /// <summary>An AI service that answers every question and accepts every document. (Reset clears this, so call it again after one.)</summary>
    private void Healthy() =>
        Upstream.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/ask" => StubUpstream.Json(HttpStatusCode.OK, """{"status":"answered","answer":"Yes.","sources":[],"action":"retrieve","trace":[]}"""),
            _ when request.Method == HttpMethod.Post => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":1}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };

    public void Dispose() => _limited.Dispose();

    /// <summary>A person on the limited application. The test user helper uses the main factory, so sign up here directly.</summary>
    private Task<TestUser> SignUpAsync() => TestAccounts.SignUpAsync(_limited);

    private static async Task<string> Title(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    private static Task<HttpResponseMessage> Upload(TestUser user, string name, string content = "# x") => user.Client.PostAsJsonAsync("/api/documents", new { name, content });

    private static Task<HttpResponseMessage> Ask(TestUser user, Guid? conversationId = null) =>
        user.Client.PostAsJsonAsync("/api/ask", new { question = "What is this?", conversationId });

    // ----------------------------------------------------------------- documents

    [Fact]
    public async Task A_person_cannot_hold_more_documents_than_the_limit_and_the_refusal_sends_nothing_upstream()
    {
        var user = await SignUpAsync();
        Assert.Equal(HttpStatusCode.Created, (await Upload(user, "a.md")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(user, "b.md")).StatusCode);
        Upstream.Reset();
        Healthy();

        var third = await Upload(user, "c.md");

        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Contains("limit of 2 documents", await Title(third));
        Assert.Empty(Upstream.Calls);
        Assert.Equal(2, await _factory.DbFor(user.Id, db => db.Documents.CountAsync()));
    }

    [Fact]
    public async Task Replacing_a_document_at_the_limit_is_allowed_because_it_adds_no_document()
    {
        var user = await SignUpAsync();
        await Upload(user, "a.md");
        await Upload(user, "b.md");

        var replaced = await Upload(user, "a.md", "# new text");

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_document_makes_room()
    {
        var user = await SignUpAsync();
        await Upload(user, "a.md");
        await Upload(user, "b.md");
        await user.Client.DeleteAsync("/api/documents/a.md");

        Assert.Equal(HttpStatusCode.Created, (await Upload(user, "c.md")).StatusCode);
    }

    [Fact]
    public async Task Total_text_is_limited_and_the_refusal_says_how_far_over_it_would_go()
    {
        var user = await SignUpAsync();
        Assert.Equal(HttpStatusCode.Created, (await Upload(user, "a.md", new string('a', 60))).StatusCode);

        var tooMuch = await Upload(user, "b.md", new string('b', 41)); // 101 characters in all

        Assert.Equal(HttpStatusCode.Forbidden, tooMuch.StatusCode);
        var title = await Title(tooMuch);
        Assert.Contains("101", title);
        Assert.Contains("100", title);
        Assert.Equal(HttpStatusCode.Created, (await Upload(user, "b.md", new string('b', 40))).StatusCode); // exactly at the limit is fine
    }

    [Fact]
    public async Task Replacing_a_document_counts_only_the_difference_in_size()
    {
        var user = await SignUpAsync();
        await Upload(user, "a.md", new string('a', 90));

        // 90 stored; replacing it with 100 characters makes 100 in all, not 190.
        Assert.Equal(HttpStatusCode.OK, (await Upload(user, "a.md", new string('a', 100))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(user, "a.md", new string('a', 101))).StatusCode);
    }

    [Fact]
    public async Task One_persons_documents_do_not_use_up_anothers_allowance()
    {
        var alice = await SignUpAsync();
        var bob = await SignUpAsync();
        await Upload(alice, "a.md", new string('a', 100));
        await Upload(alice, "b.md");

        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(alice, "c.md")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(bob, "a.md", new string('b', 100))).StatusCode);
    }

    // ----------------------------------------------------------------- conversations

    [Fact]
    public async Task A_person_cannot_start_more_conversations_than_the_limit_and_the_model_is_not_asked()
    {
        var user = await SignUpAsync();
        Assert.Equal(HttpStatusCode.OK, (await Ask(user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ask(user)).StatusCode);
        Upstream.Reset();
        Healthy();

        var third = await Ask(user);

        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Contains("limit of 2 conversations", await Title(third));
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task A_full_conversation_refuses_another_turn_but_a_new_one_can_still_be_started_elsewhere()
    {
        var user = await SignUpAsync();
        var first = await Ask(user);
        var conversationId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Ask(user, conversationId)).StatusCode); // 4 turns: at the limit
        Upstream.Reset();
        Healthy();

        var overflow = await Ask(user, conversationId);

        Assert.Equal(HttpStatusCode.Forbidden, overflow.StatusCode);
        Assert.Contains("full", await Title(overflow));
        Assert.Empty(Upstream.Calls);
        Assert.Equal(HttpStatusCode.OK, (await Ask(user)).StatusCode); // the second of two allowed conversations
    }

    [Fact]
    public async Task Deleting_a_conversation_makes_room_for_another()
    {
        var user = await SignUpAsync();
        var first = await Ask(user);
        await Ask(user);
        var conversationId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conversationId").GetGuid();
        await user.Client.DeleteAsync($"/api/conversations/{conversationId}");

        Assert.Equal(HttpStatusCode.OK, (await Ask(user)).StatusCode);
    }

    [Fact]
    public async Task One_persons_conversations_do_not_use_up_anothers_allowance()
    {
        var alice = await SignUpAsync();
        var bob = await SignUpAsync();
        await Ask(alice);
        await Ask(alice);

        Assert.Equal(HttpStatusCode.Forbidden, (await Ask(alice)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ask(bob)).StatusCode);
    }

    // ----------------------------------------------------------------- the defaults

    [Fact]
    public void The_defaults_are_generous_for_a_person_and_bounded_for_a_server()
    {
        var defaults = new NotebookApi.Infrastructure.QuotaOptions();

        Assert.Equal(50, defaults.MaxDocuments);
        Assert.Equal(2_000_000, defaults.MaxTotalDocumentChars);
        Assert.Equal(200, defaults.MaxConversations);
        Assert.Equal(400, defaults.MaxMessagesPerConversation);
    }
}
