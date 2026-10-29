using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotebookApi.Contracts;
using NotebookApi.Data;
using NotebookApi.Services;

namespace NotebookApi.Tests;

/// <summary>The database is the source of truth for documents; the AI service's index is derived from it and kept in step.</summary>
public sealed class DocumentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private StubUpstream Upstream => _factory.Upstream;

    public DocumentTests(ApiFactory factory)
    {
        _factory = factory;
        factory.ResetAsync().GetAwaiter().GetResult();
        _client = factory.CreateClient();
        Healthy(chunks: 3);
    }

    /// <summary>An AI service that accepts everything: uploads report the given chunk count, deletes succeed, the listing is empty.</summary>
    private void Healthy(int chunks) =>
        Upstream.Respond = request => request.Method.Method switch
        {
            "POST" => StubUpstream.Json(HttpStatusCode.Created, $$"""{"name":"x","chunks":{{chunks}}}"""),
            "DELETE" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => StubUpstream.Json(HttpStatusCode.OK, "{}"),
        };

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Upload(string name, string content) => _client.PostAsJsonAsync("/api/documents", new { name, content });

    private async Task<JsonElement> Listed(string name) =>
        (await Json(await _client.GetAsync("/api/documents"))).EnumerateArray().Single(d => d.GetProperty("name").GetString() == name);

    // ----------------------------------------------------------------- adding

    [Fact]
    public async Task Adding_a_document_stores_it_sends_it_upstream_and_marks_it_Indexed()
    {
        var response = await Upload("my notes.md", "# Notes\n\nSome text.");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/documents/my%20notes.md", response.Headers.Location?.ToString());
        var body = await Json(response);
        Assert.Equal("Indexed", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("chunks").GetInt32());
        Assert.Equal("# Notes\n\nSome text.".Length, body.GetProperty("chars").GetInt32());

        var sent = JsonDocument.Parse(Assert.Single(Upstream.Calls).Body).RootElement;
        Assert.Equal("my notes.md", sent.GetProperty("name").GetString());
        Assert.Equal("# Notes\n\nSome text.", sent.GetProperty("content").GetString());

        var stored = await _factory.Db(db => db.Documents.SingleAsync());
        Assert.Equal("# Notes\n\nSome text.", stored.Content);
        Assert.Equal(DocumentStatus.Indexed, stored.Status);
    }

    [Fact]
    public async Task Replacing_a_document_returns_200_keeps_one_row_and_moves_UpdatedAt()
    {
        await Upload("a.md", "# One\n\nfirst");
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await Upload("a.md", "# Two\n\nsecond version");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var info = await Listed("a.md");
        Assert.Equal(ManualClock.Start, info.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(ManualClock.Start.AddMinutes(5), info.GetProperty("updatedAt").GetDateTimeOffset());
        var stored = await _factory.Db(db => db.Documents.SingleAsync());
        Assert.Equal("# Two\n\nsecond version", stored.Content);
    }

    [Fact]
    public async Task The_list_comes_from_the_database_sorted_by_name_without_calling_the_AI_service()
    {
        await Upload("b.md", "# B");
        await Upload("a.md", "# A");
        Upstream.Reset();

        var list = await Json(await _client.GetAsync("/api/documents"));

        Assert.Equal(["a.md", "b.md"], list.EnumerateArray().Select(d => d.GetProperty("name").GetString()!).ToArray());
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task The_document_name_is_unique_in_the_database_itself()
    {
        await Upload("a.md", "# A");

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.Db(async db =>
        {
            db.Documents.Add(new StoredDocument { Name = "a.md", Content = "duplicate" });
            await db.SaveChangesAsync();
        }));
    }

    // ----------------------------------------------------------------- when the AI service cannot index it

    [Theory]
    [InlineData("unreachable", HttpStatusCode.ServiceUnavailable, "Unavailable")]
    [InlineData("busy", HttpStatusCode.ServiceUnavailable, "Busy")]
    [InlineData("broken", HttpStatusCode.BadGateway, "BadGateway")]
    [InlineData("slow", HttpStatusCode.GatewayTimeout, "Timeout")]
    public async Task A_failed_upload_keeps_the_text_as_Failed_and_still_reports_the_error(string how, HttpStatusCode expected, string recordedKind)
    {
        Upstream.Respond = _ => how switch
        {
            "unreachable" => throw new HttpRequestException("connection refused: secret-detail"),
            "busy" => StubUpstream.Json(HttpStatusCode.ServiceUnavailable, """{"detail":"secret-detail"}"""),
            "broken" => StubUpstream.Json(HttpStatusCode.InternalServerError, "secret-detail"),
            _ => throw new TaskCanceledException("secret-detail"),
        };

        var response = await Upload("a.md", "# A\n\ntext that must not be lost");

        Assert.Equal(expected, response.StatusCode);
        var info = await Listed("a.md");
        Assert.Equal("Failed", info.GetProperty("status").GetString());
        Assert.Equal(recordedKind, info.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("chunks").ValueKind);
        Assert.Equal("# A\n\ntext that must not be lost", (await _factory.Db(db => db.Documents.SingleAsync())).Content);
        Assert.DoesNotContain("secret-detail", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("secret-detail", info.ToString());
    }

    [Fact]
    public async Task Reindexing_a_Failed_document_sends_the_stored_text_and_marks_it_Indexed()
    {
        Upstream.Respond = _ => throw new HttpRequestException("down");
        await Upload("a.md", "# A\n\nstored text");
        Upstream.Reset();
        Healthy(chunks: 7);

        var response = await _client.PostAsync("/api/documents/a.md/reindex", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Indexed", body.GetProperty("status").GetString());
        Assert.Equal(7, body.GetProperty("chunks").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.Equal("# A\n\nstored text", JsonDocument.Parse(Assert.Single(Upstream.Calls).Body).RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Reindexing_an_unknown_document_is_404_and_a_bad_name_is_400_with_no_upstream_call()
    {
        var missing = await _client.PostAsync("/api/documents/nope.md/reindex", null);
        var invalid = await _client.PostAsync("/api/documents/nope.txt/reindex", null);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(Upstream.Calls);
    }

    [Fact]
    public async Task A_cancelled_upload_is_recorded_as_Failed_not_left_Pending()
    {
        using var cancel = new CancellationTokenSource();
        Upstream.Respond = _ =>
        {
            cancel.Cancel(); // the caller goes away while the AI service is working
            throw new TaskCanceledException();
        };

        using var scope = _factory.Services.CreateScope();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => documents.UpsertAsync(new DocumentIn { Name = "a.md", Content = "# A" }, cancel.Token));

        var stored = await _factory.Db(db => db.Documents.SingleAsync());
        Assert.Equal(DocumentStatus.Failed, stored.Status);
        Assert.Equal("Cancelled", stored.Error);
    }

    // ----------------------------------------------------------------- deleting

    [Fact]
    public async Task Deleting_removes_it_upstream_with_the_name_escaped_and_then_from_the_database()
    {
        await Upload("my notes.md", "# Notes");
        Upstream.Reset();
        Healthy(chunks: 1);

        var response = await _client.DeleteAsync("/api/documents/my%20notes.md");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var call = Assert.Single(Upstream.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal("/documents/my%20notes.md", call.PathAndQuery);
        Assert.Empty(await _factory.Db(db => db.Documents.ToListAsync()));
    }

    [Fact]
    public async Task If_the_AI_service_cannot_delete_the_row_is_kept_so_the_delete_can_be_repeated()
    {
        await Upload("a.md", "# A");
        Upstream.Respond = _ => throw new HttpRequestException("down");

        var failed = await _client.DeleteAsync("/api/documents/a.md");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Single(await _factory.Db(db => db.Documents.ToListAsync()));

        Healthy(chunks: 1);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/documents/a.md")).StatusCode);
        Assert.Empty(await _factory.Db(db => db.Documents.ToListAsync()));
    }

    [Fact]
    public async Task A_document_the_AI_service_already_lacks_is_still_deleted_here()
    {
        await Upload("a.md", "# A");
        Upstream.Respond = _ => StubUpstream.Json(HttpStatusCode.NotFound, """{"detail":"No such document"}""");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/documents/a.md")).StatusCode);
        Assert.Empty(await _factory.Db(db => db.Documents.ToListAsync()));
    }

    [Fact]
    public async Task Deleting_an_unknown_document_is_a_404_problem_without_calling_the_AI_service()
    {
        var response = await _client.DeleteAsync("/api/documents/missing.md");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(Upstream.Calls);
    }

    // ----------------------------------------------------------------- sync

    [Fact]
    public async Task Sync_removes_what_only_the_index_has_then_resends_every_stored_document_in_that_order()
    {
        await Upload("a.md", "# A");
        await Upload("b.md", "# B");
        Upstream.Reset();
        Upstream.Respond = request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/documents") => StubUpstream.Json(HttpStatusCode.OK, """{"a.md":1,"orphan.md":2,"zz-orphan.md":4}"""),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":9}"""),
        };

        var response = await _client.PostAsync("/api/documents/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(2, body.GetProperty("reindexed").GetInt32());
        Assert.Equal(["orphan.md", "zz-orphan.md"], body.GetProperty("removedFromIndex").EnumerateArray().Select(n => n.GetString()!).ToArray());

        Assert.Equal(
            ["GET /documents", "DELETE /documents/orphan.md", "DELETE /documents/zz-orphan.md", "POST /documents", "POST /documents"],
            Upstream.Calls.Select(c => $"{c.Method} {c.PathAndQuery}").ToArray());
        Assert.Equal(["a.md", "b.md"], Upstream.Calls.Where(c => c.Method == HttpMethod.Post).Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("name").GetString()!).ToArray());
        Assert.All(await _factory.Db(db => db.Documents.ToListAsync()), d => { Assert.Equal(DocumentStatus.Indexed, d.Status); Assert.Equal(9, d.Chunks); });
    }

    [Fact]
    public async Task Sync_with_the_AI_service_unreachable_fails_before_changing_anything()
    {
        await Upload("a.md", "# A");
        var before = await _factory.Db(db => db.Documents.AsNoTracking().SingleAsync());
        Upstream.Respond = _ => throw new HttpRequestException("down");

        var response = await _client.PostAsync("/api/documents/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var after = await _factory.Db(db => db.Documents.AsNoTracking().SingleAsync());
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Chunks, after.Chunks);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task A_sync_that_fails_part_way_marks_that_document_Failed_and_running_it_again_finishes()
    {
        await Upload("a.md", "# A");
        await Upload("b.md", "# B");
        Upstream.Reset();
        Upstream.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return StubUpstream.Json(HttpStatusCode.OK, "{}");
            var name = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.GetProperty("name").GetString();
            return name == "b.md" ? throw new HttpRequestException("down") : StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":2}""");
        };

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.PostAsync("/api/documents/sync", null)).StatusCode);
        Assert.Equal("Indexed", (await Listed("a.md")).GetProperty("status").GetString());
        Assert.Equal("Failed", (await Listed("b.md")).GetProperty("status").GetString());

        Healthy(chunks: 5);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/api/documents/sync", null)).StatusCode);
        Assert.All(await _factory.Db(db => db.Documents.ToListAsync()), d => Assert.Equal(DocumentStatus.Indexed, d.Status));
    }

    // ----------------------------------------------------------------- overlapping writes

    [Fact]
    public async Task Overlapping_uploads_of_the_same_name_are_applied_one_after_the_other()
    {
        using var release = new ManualResetEventSlim();
        var firstSeen = new TaskCompletionSource();
        var calls = 0;
        Upstream.Respond = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstSeen.SetResult();
                release.Wait(TimeSpan.FromSeconds(10)); // the first upload is still being indexed
            }

            return StubUpstream.Json(HttpStatusCode.Created, """{"name":"x","chunks":1}""");
        };

        var first = Upload("a.md", "# first version");
        await firstSeen.Task;
        var second = Upload("a.md", "# second version");
        await Task.Delay(400); // time for the second request to reach the gate if there were none

        Assert.Single(Upstream.Calls); // the second upload waits; it is not sent while the first is in flight
        release.Set();
        await Task.WhenAll(first, second);

        Assert.Equal(["# first version", "# second version"], Upstream.Calls.Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("content").GetString()!).ToArray());
        var stored = await _factory.Db(db => db.Documents.SingleAsync());
        Assert.Equal("# second version", stored.Content); // database and index agree: the last upload won in both
        Assert.Equal(DocumentStatus.Indexed, stored.Status);
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.OK], new[] { (await first).StatusCode, (await second).StatusCode });
    }
}
