using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotebookApi.Data;
using NotebookApi.Services;

namespace NotebookApi.Tests;

public sealed class ConversationTests : IClassFixture<ApiFactory>
{
    private const string Answered = """{"status":"answered","answer":"Chroma.","sources":[{"id":"a.md#0","source":"a.md","heading":"Setup","snippet":"Chroma is the store."}],"action":"retrieve","trace":[]}""";

    private readonly ApiFactory _factory;
    private readonly TestUser _user;
    private readonly HttpClient _client;
    private StubUpstream Upstream => _factory.Upstream;

    public ConversationTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _user = factory.SignUp();
        _client = _user.Client;
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, Answered);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Ask(string question, Guid? conversationId = null, string mode = "pipeline") =>
        _client.PostAsJsonAsync("/api/ask", new { question, conversationId, mode });

    private async Task<Guid> StartConversation(string question)
    {
        var body = await Json(await Ask(question));
        return body.GetProperty("conversationId").GetGuid();
    }

    // ----------------------------------------------------------------- saving an exchange

    [Fact]
    public async Task Asking_without_a_conversation_starts_one_and_saves_the_question_and_the_answer()
    {
        var response = await Ask("Which vector database is used?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("answered", body.GetProperty("status").GetString());
        var id = body.GetProperty("conversationId").GetGuid();

        var conversation = await Json(await _client.GetAsync($"/api/conversations/{id}"));
        Assert.Equal("Which vector database is used?", conversation.GetProperty("title").GetString());
        var messages = conversation.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()!).ToArray());
        Assert.Equal("Which vector database is used?", messages[0].GetProperty("text").GetString());
        Assert.Equal("Chroma.", messages[1].GetProperty("text").GetString());
        Assert.Equal("answered", messages[1].GetProperty("status").GetString());
        Assert.Equal("a.md#0", messages[1].GetProperty("sources")[0].GetProperty("id").GetString());
        Assert.Equal("Setup", messages[1].GetProperty("sources")[0].GetProperty("heading").GetString());
        Assert.Equal(messages[1].GetProperty("id").GetInt32(), body.GetProperty("messageId").GetInt32());
    }

    [Fact]
    public async Task The_conversation_id_is_used_to_save_the_exchange_but_never_sent_to_the_AI_service()
    {
        var id = await StartConversation("first question");
        Upstream.Reset();
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, Answered);

        await Ask("second question", id);

        var sent = JsonDocument.Parse(Assert.Single(Upstream.Calls).Body).RootElement;
        Assert.False(sent.TryGetProperty("conversationId", out _));
        Assert.Equal(["question", "k", "mode"], sent.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Continuing_a_conversation_appends_the_turns_in_order_and_keeps_the_title()
    {
        var id = await StartConversation("first question");
        await Ask("second question", id);
        await Ask("third question", id);

        var conversation = await Json(await _client.GetAsync($"/api/conversations/{id}"));
        Assert.Equal("first question", conversation.GetProperty("title").GetString());
        var texts = conversation.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(["first question", "Chroma.", "second question", "Chroma.", "third question", "Chroma."], texts);
    }

    [Fact]
    public async Task A_clarifying_question_is_saved_with_its_status_and_action()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK,
            """{"status":"clarify","answer":"Which limit do you mean?","sources":[],"action":"clarify","trace":["route: clarify"]}""");

        var id = (await Json(await Ask("What was the limit?", mode: "agent"))).GetProperty("conversationId").GetGuid();

        var reply = (await Json(await _client.GetAsync($"/api/conversations/{id}"))).GetProperty("messages")[1];
        Assert.Equal("clarify", reply.GetProperty("status").GetString());
        Assert.Equal("clarify", reply.GetProperty("action").GetString());
        Assert.Equal("Which limit do you mean?", reply.GetProperty("text").GetString());
        Assert.Equal(0, reply.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task The_request_id_is_stored_with_both_turns_so_a_saved_answer_can_be_traced_to_its_logs()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask") { Content = JsonContent.Create(new { question = "Which store?" }) };
        request.Headers.Add("X-Request-ID", "trace-42");

        await _client.SendAsync(request);

        var messages = await _factory.DbFor(_user.Id, db => db.Messages.ToListAsync());
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.Equal("trace-42", m.RequestId));
    }

    // ----------------------------------------------------------------- what is not saved

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_ask_saves_nothing(HttpStatusCode upstream)
    {
        Upstream.Respond = _ => StubUpstream.Json(upstream, "{}");

        var response = await Ask("Which vector database is used?");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Empty(await _factory.DbFor(_user.Id, db => db.Conversations.ToListAsync()));
        Assert.Empty(await _factory.DbFor(_user.Id, db => db.Messages.ToListAsync()));
    }

    [Fact]
    public async Task A_failed_ask_in_an_existing_conversation_leaves_it_unchanged()
    {
        var id = await StartConversation("first question");
        var before = await _factory.DbFor(_user.Id, db => db.Conversations.AsNoTracking().SingleAsync());
        _factory.Clock.Advance(TimeSpan.FromHours(1));
        Upstream.Respond = _ => throw new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Ask("second question", id)).StatusCode);

        Assert.Equal(2, await _factory.DbFor(_user.Id, db => db.Messages.CountAsync()));
        Assert.Equal(before.UpdatedAt, (await _factory.DbFor(_user.Id, db => db.Conversations.AsNoTracking().SingleAsync())).UpdatedAt);
    }

    [Fact]
    public async Task An_unknown_conversation_is_a_404_before_any_model_call_and_nothing_is_saved()
    {
        var response = await Ask("Which vector database is used?", Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(Upstream.Calls);
        Assert.Empty(await _factory.DbFor(_user.Id, db => db.Messages.ToListAsync()));
    }

    [Fact]
    public async Task A_conversation_deleted_while_the_model_is_answering_is_a_404_and_leaves_no_stray_turns()
    {
        var id = await StartConversation("first question");
        Upstream.Respond = _ =>
        {
            _factory.DbFor(_user.Id, db => db.Conversations.ExecuteDeleteAsync()).GetAwaiter().GetResult(); // deleted in another request, mid-answer
            return StubUpstream.Json(HttpStatusCode.OK, Answered);
        };

        var response = await Ask("second question", id);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await _factory.DbFor(_user.Id, db => db.Messages.ToListAsync()));
    }

    [Fact]
    public async Task No_question_or_answer_text_is_written_to_the_logs_even_though_it_is_stored()
    {
        await Ask("a-very-distinctive-question-text");

        Assert.NotEmpty(_factory.Logs.Entries);
        Assert.DoesNotContain(_factory.Logs.Entries, e => e.Message.Contains("a-very-distinctive-question-text") || e.Message.Contains("Chroma."));
        Assert.Equal(2, await _factory.DbFor(_user.Id, db => db.Messages.CountAsync()));
    }

    // ----------------------------------------------------------------- listing, reading, deleting

    [Fact]
    public async Task The_list_shows_the_most_recently_active_conversation_first_with_its_message_count()
    {
        var older = await StartConversation("older one");
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var newer = await StartConversation("newer one");
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Ask("another turn", older); // the older conversation becomes the most recently active

        var list = (await Json(await _client.GetAsync("/api/conversations"))).EnumerateArray().ToList();

        Assert.Equal([older, newer], list.Select(c => c.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal([4, 2], list.Select(c => c.GetProperty("messages").GetInt32()).ToArray());
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(0, 1)]     // clamped up
    [InlineData(-5, 1)]
    [InlineData(100000, 3)] // clamped down to 100, which still returns all three
    public async Task The_list_limit_is_clamped(int limit, int expected)
    {
        for (var i = 0; i < 3; i++)
        {
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
            await StartConversation($"question {i}");
        }

        var list = await Json(await _client.GetAsync($"/api/conversations?limit={limit}"));

        Assert.Equal(expected, list.GetArrayLength());
    }

    [Theory]
    [InlineData("   Which\n\t  store   is  used?  ", "Which store is used?")]
    [InlineData("short", "short")]
    public void Titles_collapse_whitespace(string question, string expected) =>
        Assert.Equal(expected, ConversationService.MakeTitle(question));

    [Fact]
    public void A_long_question_makes_a_title_that_fits_the_column_and_ends_with_an_ellipsis()
    {
        var title = ConversationService.MakeTitle(string.Join(' ', Enumerable.Repeat("word", 100)));

        Assert.True(title.Length <= ConversationService.MaxTitleChars);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public async Task Reading_an_unknown_or_malformed_conversation_id_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/conversations/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/conversations/not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_conversation_deletes_its_turns_and_only_its_turns()
    {
        var doomed = await StartConversation("doomed");
        var kept = await StartConversation("kept");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/conversations/{doomed}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/conversations/{doomed}")).StatusCode);
        var remaining = await _factory.DbFor(_user.Id, db => db.Messages.ToListAsync());
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, m => Assert.Equal(kept, m.ConversationId));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/conversations/{doomed}")).StatusCode);
    }
}
