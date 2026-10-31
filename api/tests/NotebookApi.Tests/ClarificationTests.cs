using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NotebookApi.Tests;

/// <summary>
/// When a person answers a clarifying question, the AI service (which sees one question at a time) must be given the original
/// question together with the answer. Measured on retrieval in ai-service/evals/clarify_followup_eval.py: reply alone found the
/// answer in 21 of 30 cases, question plus reply in 26. The request says so explicitly (answersClarification); nothing is inferred.
/// </summary>
public sealed class ClarificationTests : IClassFixture<ApiFactory>
{
    private const string Clarify = """{"status":"clarify","answer":"Which one do you mean?","sources":[],"action":"clarify","trace":["route: clarify"]}""";
    private const string Answered = """{"status":"answered","answer":"Chroma.","sources":[{"id":"a.md#0","source":"a.md","heading":"Setup","snippet":"Chroma is the store."}],"action":"retrieve","trace":[]}""";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private StubUpstream Upstream => _factory.Upstream;
    private readonly Queue<string> _replies = new();

    public ClarificationTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _client = factory.CreateClient();
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, _replies.Dequeue());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Ask(string question, Guid? conversationId = null, bool answersClarification = false, string mode = "agent") =>
        _client.PostAsJsonAsync("/api/ask", new { question, conversationId, answersClarification, mode });

    private string[] QuestionsSentUpstream() =>
        Upstream.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("question").GetString()!).ToArray();

    /// <summary>A conversation whose last turn is a clarifying question.</summary>
    private async Task<Guid> AwaitingAnswer(string vagueQuestion)
    {
        _replies.Enqueue(Clarify);
        var body = await Json(await Ask(vagueQuestion));
        Assert.Equal("clarify", body.GetProperty("status").GetString());
        return body.GetProperty("conversationId").GetGuid();
    }

    // ----------------------------------------------------------------- the combined question

    [Fact]
    public async Task The_AI_service_gets_the_original_question_and_the_answer_together()
    {
        var id = await AwaitingAnswer("How should that be handled?");
        _replies.Enqueue(Answered);

        var response = await Ask("relative dates", id, answersClarification: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["How should that be handled?", "How should that be handled? relative dates"], QuestionsSentUpstream());
        Assert.Equal("answered", (await Json(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_history_keeps_what_the_person_typed_not_the_combined_text()
    {
        var id = await AwaitingAnswer("How should that be handled?");
        _replies.Enqueue(Answered);
        await Ask("relative dates", id, answersClarification: true);

        var conversation = await Json(await _client.GetAsync($"/api/conversations/{id}"));

        var texts = conversation.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString()).ToArray();
        Assert.Equal(["How should that be handled?", "Which one do you mean?", "relative dates", "Chroma."], texts);
    }

    [Fact]
    public async Task A_second_clarifying_question_keeps_the_whole_chain_not_only_the_last_answer()
    {
        var id = await AwaitingAnswer("Why was it so high?");
        _replies.Enqueue(Clarify); // still too vague after the first answer
        await Ask("the number", id, answersClarification: true);
        _replies.Enqueue(Answered);

        await Ask("recall at 200 thousand", id, answersClarification: true);

        Assert.Equal(
            ["Why was it so high?", "Why was it so high? the number", "Why was it so high? the number recall at 200 thousand"],
            QuestionsSentUpstream());
    }

    [Fact]
    public async Task Earlier_finished_exchanges_are_not_mixed_into_a_later_clarification()
    {
        _replies.Enqueue(Answered);
        var id = (await Json(await Ask("Which store is used?"))).GetProperty("conversationId").GetGuid();
        _replies.Enqueue(Clarify);
        await Ask("What about that one?", id);
        _replies.Enqueue(Answered);

        await Ask("the reranker", id, answersClarification: true);

        Assert.Equal("What about that one? the reranker", QuestionsSentUpstream()[^1]);
    }

    [Fact]
    public async Task Without_the_flag_a_new_question_after_a_clarifying_question_is_sent_as_it_is()
    {
        var id = await AwaitingAnswer("How does this compare?");
        _replies.Enqueue(Answered);

        await Ask("What is the embedding limit?", id); // the person ignored the clarifying question and asked something else

        Assert.Equal("What is the embedding limit?", QuestionsSentUpstream()[^1]);
    }

    // ----------------------------------------------------------------- refusals

    [Fact]
    public async Task Answering_a_clarification_needs_a_conversation()
    {
        var response = await Ask("relative dates", conversationId: null, answersClarification: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task An_unknown_conversation_is_a_404()
    {
        var response = await Ask("relative dates", Guid.NewGuid(), answersClarification: true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task A_conversation_that_is_not_waiting_for_an_answer_is_a_409_and_nothing_is_saved()
    {
        _replies.Enqueue(Answered);
        var id = (await Json(await Ask("Which store is used?"))).GetProperty("conversationId").GetGuid();
        var before = await _factory.Db(db => db.Messages.CountAsync());
        Upstream.Reset();

        var response = await Ask("relative dates", id, answersClarification: true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(Upstream.Calls);
        Assert.Equal(before, await _factory.Db(db => db.Messages.CountAsync()));
    }

    [Fact]
    public async Task A_combined_question_over_the_length_limit_is_a_400_before_any_model_call()
    {
        var id = await AwaitingAnswer(new string('q', 600));
        Upstream.Reset();

        var response = await Ask(new string('r', 600), id, answersClarification: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task A_failed_attempt_saves_nothing_so_the_same_answer_can_be_sent_again()
    {
        var id = await AwaitingAnswer("How should that be handled?");
        Upstream.Respond = _ => throw new HttpRequestException("down");
        var messagesBefore = await _factory.Db(db => db.Messages.CountAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Ask("relative dates", id, answersClarification: true)).StatusCode);
        Assert.Equal(messagesBefore, await _factory.Db(db => db.Messages.CountAsync()));

        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, Answered);
        var retry = await Ask("relative dates", id, answersClarification: true); // still waiting for the answer, so the retry is accepted
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    // ----------------------------------------------------------------- the status on the wire

    [Theory]
    [InlineData("answered")]
    [InlineData("not_found")]
    [InlineData("uncited")]
    [InlineData("clarify")]
    public async Task Every_status_keeps_its_wire_name_in_the_reply_and_in_the_history(string status)
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, $$"""{"status":"{{status}}","answer":"x","sources":[],"action":"retrieve","trace":[]}""");

        var reply = await Json(await Ask("Which store is used?", mode: "pipeline"));
        var history = await Json(await _client.GetAsync($"/api/conversations/{reply.GetProperty("conversationId").GetGuid()}"));

        Assert.Equal(status, reply.GetProperty("status").GetString());
        Assert.Equal(status, history.GetProperty("messages")[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, history.GetProperty("messages")[0].GetProperty("status").ValueKind);
    }

    [Fact]
    public async Task A_status_the_API_does_not_know_is_a_502_and_is_not_saved()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"status":"confident","answer":"x","sources":[],"action":"retrieve","trace":[]}""");

        var response = await Ask("Which store is used?", mode: "pipeline");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(await _factory.Db(db => db.Messages.ToListAsync()));
    }
}
