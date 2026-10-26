using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotebookApi.Contracts;

namespace NotebookApi.Tests;

public sealed class ApiTests : IClassFixture<ApiFactory>
{
    private const string AskReply = """{"status":"answered","answer":"Chroma.","sources":[{"id":"a.md#0","source":"a.md","heading":"Setup","snippet":"Chroma is the store."}],"action":"retrieve","trace":[]}""";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private StubUpstream Upstream => _factory.Upstream;

    public ApiTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Upstream.Reset();
        _client = factory.CreateClient();
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    // ----------------------------------------------------------------- /api/ask: the contract

    [Fact]
    public async Task Ask_sends_the_documented_contract_upstream_and_returns_the_reply()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, AskReply);

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "Which vector database is used?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(Upstream.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/ask", call.PathAndQuery);
        var sent = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("Which vector database is used?", sent.GetProperty("question").GetString());
        Assert.Equal(4, sent.GetProperty("k").GetInt32());
        Assert.Equal("pipeline", sent.GetProperty("mode").GetString());

        var body = await ReadJson(response);
        Assert.Equal("answered", body.GetProperty("status").GetString());
        Assert.Equal("a.md#0", body.GetProperty("sources")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Ask_in_agent_mode_passes_a_clarifying_question_through()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK,
            """{"status":"clarify","answer":"Which limit do you mean?","sources":[],"action":"clarify","trace":["route: clarify"]}""");

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "What was the limit?", mode = "agent", k = 6 });

        var sent = JsonDocument.Parse(Assert.Single(Upstream.Calls).Body).RootElement;
        Assert.Equal("agent", sent.GetProperty("mode").GetString());
        Assert.Equal(6, sent.GetProperty("k").GetInt32());
        var body = await ReadJson(response);
        Assert.Equal("clarify", body.GetProperty("status").GetString());
        Assert.Equal("Which limit do you mean?", body.GetProperty("answer").GetString());
        Assert.Equal("route: clarify", body.GetProperty("trace")[0].GetString());
    }

    [Theory]
    [InlineData("""{"question":"ab"}""")]                                      // shorter than 3 characters
    [InlineData("""{"question":"a fine question","k":0}""")]                   // k below 1
    [InlineData("""{"question":"a fine question","k":99}""")]                  // k above 10
    [InlineData("""{"question":"a fine question","mode":"turbo"}""")]          // unknown mode
    [InlineData("""{"k":4}""")]                                                // missing question
    public async Task Ask_rejects_invalid_input_at_the_edge_without_calling_the_AI_service(string json)
    {
        var response = await _client.PostAsync("/api/ask", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task Ask_rejects_an_over_long_question()
    {
        var response = await _client.PostAsJsonAsync("/api/ask", new { question = new string('x', Limits.MaxQuestionChars + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    // ----------------------------------------------------------------- upstream failures

    [Fact]
    public async Task A_busy_AI_service_becomes_503_with_its_Retry_After_and_leaks_nothing()
    {
        Upstream.Respond = _ =>
        {
            var busy = StubUpstream.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"secret-internal-detail"}""");
            busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return busy;
        };

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("7", response.Headers.GetValues("Retry-After").Single());
        Assert.DoesNotContain("secret-internal-detail", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_busy_AI_service_without_Retry_After_gets_a_default()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.ServiceUnavailable, "{}");

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("10", response.Headers.GetValues("Retry-After").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]   // the AI service rejected something this API validated: contract drift
    public async Task An_AI_service_failure_becomes_502(HttpStatusCode upstream)
    {
        Upstream.Respond = _ => StubUpstream.Json(upstream, """{"detail":"secret-internal-detail"}""");

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("secret-internal-detail", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unreadable_AI_service_body_becomes_502()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, "this is not json");

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_timeout_becomes_504()
    {
        Upstream.Respond = _ => throw new TaskCanceledException("timed out", new TimeoutException());

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task An_unreachable_AI_service_becomes_503_with_Retry_After()
    {
        Upstream.Respond = _ => throw new HttpRequestException("connection refused");

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.GetValues("Retry-After").Single());
    }

    // ----------------------------------------------------------------- documents

    [Fact]
    public async Task Adding_a_document_forwards_name_and_content_and_returns_201_with_a_location()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.Created, """{"name":"my notes.md","chunks":3}""");

        var response = await _client.PostAsJsonAsync("/api/documents", new { name = "my notes.md", content = "# Notes\n\nSome text." });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/documents/my%20notes.md", response.Headers.Location?.ToString());
        var sent = JsonDocument.Parse(Assert.Single(Upstream.Calls).Body).RootElement;
        Assert.Equal("my notes.md", sent.GetProperty("name").GetString());
        Assert.Equal("# Notes\n\nSome text.", sent.GetProperty("content").GetString());
        Assert.Equal(3, (await ReadJson(response)).GetProperty("chunks").GetInt32());
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("a/b.md")]
    [InlineData("../secrets.md")]
    [InlineData(".md")]
    public async Task Adding_a_document_with_a_bad_name_is_rejected_before_any_upstream_call(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/documents", new { name, content = "# x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task Adding_an_empty_or_oversized_document_is_rejected()
    {
        var empty = await _client.PostAsJsonAsync("/api/documents", new { name = "a.md", content = "" });
        var huge = await _client.PostAsJsonAsync("/api/documents", new { name = "a.md", content = new string('x', Limits.MaxDocumentChars + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task Listing_documents_passes_the_counts_through()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"a.md":3,"b.md":5}""");

        var body = await ReadJson(await _client.GetAsync("/api/documents"));

        Assert.Equal(3, body.GetProperty("a.md").GetInt32());
        Assert.Equal(5, body.GetProperty("b.md").GetInt32());
    }

    [Fact]
    public async Task Deleting_escapes_the_name_in_the_upstream_path_and_returns_204()
    {
        Upstream.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var response = await _client.DeleteAsync("/api/documents/my%20notes.md");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var call = Assert.Single(Upstream.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal("/documents/my%20notes.md", call.PathAndQuery);
    }

    [Fact]
    public async Task Deleting_a_missing_document_is_a_404_problem()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.NotFound, """{"detail":"No such document"}""");

        var response = await _client.DeleteAsync("/api/documents/missing.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Deleting_with_an_invalid_name_is_rejected_before_any_upstream_call()
    {
        var response = await _client.DeleteAsync("/api/documents/notes.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Theory]
    [InlineData("notes.md%0A")]       // a trailing newline: .NET's $ matches before it, Python's stricter pattern does not
    [InlineData("notes.md%0D%0A")]
    [InlineData("no%0Ate.md")]
    public async Task Deleting_a_name_with_control_characters_is_a_400_here_not_a_502_from_the_AI_service(string encodedName)
    {
        var response = await _client.DeleteAsync($"/api/documents/{encodedName}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task Adding_a_document_whose_name_has_a_trailing_newline_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/documents", new { name = "notes.md\n", content = "# x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    // ----------------------------------------------------------------- health

    [Fact]
    public async Task Health_is_ok_when_the_AI_service_is_up()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, """{"status":"ok","documents":2,"chunks":18}""");

        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(18, body.GetProperty("ai").GetProperty("chunks").GetInt32());
    }

    [Fact]
    public async Task Health_reports_degraded_with_503_when_the_AI_service_is_down()
    {
        Upstream.Respond = _ => throw new HttpRequestException("connection refused");

        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("degraded", (await ReadJson(response)).GetProperty("status").GetString());
    }

    // ----------------------------------------------------------------- correlation id

    [Fact]
    public async Task Log_lines_written_while_handling_a_request_carry_its_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Request-ID", "log-scope-9");

        await _client.SendAsync(request);

        var line = _factory.Logs.Entries.Last(e => e.Category.EndsWith("CorrelationIdMiddleware") && e.Scope.ContainsKey("CorrelationId") && (string)e.Scope["CorrelationId"] == "log-scope-9");
        Assert.Contains("/api/health", line.Message);
    }

    [Fact]
    public void The_console_logger_is_configured_to_print_scopes_so_the_id_is_visible()
    {
        // The id lives in a logging scope, and the console logger drops scopes unless told otherwise (found in the live run).
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();
        Assert.True(configuration.GetValue<bool>("Logging:Console:IncludeScopes"));
    }

    [Fact]
    public async Task A_valid_request_id_is_echoed_and_forwarded_upstream()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, AskReply);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask") { Content = JsonContent.Create(new { question = "anything at all" }) };
        request.Headers.Add("X-Request-ID", "trace-123.abc_X");

        var response = await _client.SendAsync(request);

        Assert.Equal("trace-123.abc_X", response.Headers.GetValues("X-Request-ID").Single());
        Assert.Equal("trace-123.abc_X", Assert.Single(Upstream.Calls).Headers["X-Request-ID"]);
    }

    [Fact]
    public async Task A_request_id_is_generated_when_none_is_sent()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, AskReply);

        var response = await _client.PostAsJsonAsync("/api/ask", new { question = "anything at all" });

        var id = response.Headers.GetValues("X-Request-ID").Single();
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, Assert.Single(Upstream.Calls).Headers["X-Request-ID"]);
    }

    [Theory]
    [InlineData("bad id; with spaces")]
    [InlineData("{injected}")]
    public async Task An_unsafe_request_id_is_replaced_not_trusted(string unsafeId)
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, AskReply);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask") { Content = JsonContent.Create(new { question = "anything at all" }) };
        request.Headers.TryAddWithoutValidation("X-Request-ID", unsafeId);

        var response = await _client.SendAsync(request);

        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Request-ID").Single());
    }

    [Fact]
    public async Task A_request_id_longer_than_64_characters_is_replaced()
    {
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.OK, AskReply);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask") { Content = JsonContent.Create(new { question = "anything at all" }) };
        request.Headers.Add("X-Request-ID", new string('a', 65));

        var response = await _client.SendAsync(request);

        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Request-ID").Single());
    }

    [Fact]
    public async Task The_request_id_survives_on_error_responses_and_appears_in_the_problem_body()
    {
        // UseExceptionHandler clears the response before writing an error; the id must still be there.
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.ServiceUnavailable, "{}");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask") { Content = JsonContent.Create(new { question = "anything at all" }) };
        request.Headers.Add("X-Request-ID", "error-case-1");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("error-case-1", response.Headers.GetValues("X-Request-ID").Single());
        Assert.Equal("error-case-1", (await ReadJson(response)).GetProperty("requestId").GetString());
    }

    // ----------------------------------------------------------------- the application itself

    [Fact]
    public async Task The_OpenAPI_document_is_generated_and_lists_the_routes()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await ReadJson(response)).GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/ask", out _));
        Assert.True(paths.TryGetProperty("/api/documents", out _));
        Assert.True(paths.TryGetProperty("/api/health", out _));
    }

    [Fact]
    public void An_invalid_AI_service_URL_stops_the_application_at_startup()
    {
        using var broken = _factory.WithWebHostBuilder(builder => builder.UseSetting("AiService:BaseUrl", "not a url"));

        Assert.Throws<OptionsValidationException>(() => broken.CreateClient());
    }
}
