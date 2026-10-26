using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NotebookApi.Contracts;

namespace NotebookApi.Clients;

public interface IAiServiceClient
{
    Task<AiHealth> GetHealthAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, int>> GetDocumentsAsync(CancellationToken ct);
    Task<DocumentOut> AddDocumentAsync(DocumentIn document, CancellationToken ct);

    /// <summary>Returns false when the AI service has no such document.</summary>
    Task<bool> DeleteDocumentAsync(string name, CancellationToken ct);

    Task<AskOut> AskAsync(AskIn request, CancellationToken ct);
}

/// <summary>
/// Typed client for the Python AI service. Every failure leaves this class as an <see cref="AiServiceException"/> already classified
/// (unavailable, timeout, busy, bad gateway, contract mismatch). There are deliberately no automatic retries: the AI service already
/// backs off against the model provider, and retrying a rate-limited upstream only multiplies the load.
/// </summary>
public sealed class AiServiceClient(HttpClient http, ILogger<AiServiceClient> logger) : IAiServiceClient
{
    public Task<AiHealth> GetHealthAsync(CancellationToken ct) =>
        SendAsync<AiHealth>(HttpMethod.Get, "health", null, ct);

    public async Task<IReadOnlyDictionary<string, int>> GetDocumentsAsync(CancellationToken ct) =>
        await SendAsync<Dictionary<string, int>>(HttpMethod.Get, "documents", null, ct);

    public Task<DocumentOut> AddDocumentAsync(DocumentIn document, CancellationToken ct) =>
        SendAsync<DocumentOut>(HttpMethod.Post, "documents", JsonContent.Create(new { name = document.Name, content = document.Content }), ct);

    public async Task<bool> DeleteDocumentAsync(string name, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, $"documents/{Uri.EscapeDataString(name)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw Classify(response);
        return true;
    }

    public Task<AskOut> AskAsync(AskIn request, CancellationToken ct) =>
        SendAsync<AskOut>(HttpMethod.Post, "ask", JsonContent.Create(new { question = request.Question, k = request.K, mode = request.Mode }), ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, content, ct);
        if (!response.IsSuccessStatusCode)
            throw Classify(response);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct)
                   ?? throw new AiServiceException(UpstreamFailure.BadGateway, "The AI service returned an empty body.", (int)response.StatusCode);
        }
        catch (JsonException e)
        {
            throw new AiServiceException(UpstreamFailure.BadGateway, "The AI service returned a body that does not match the contract.", (int)response.StatusCode, inner: e);
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a TaskCanceledException; the caller's cancellation is handled by ASP.NET Core.
            throw new AiServiceException(UpstreamFailure.Timeout, "The AI service did not respond in time.", inner: e);
        }
        catch (HttpRequestException e)
        {
            throw new AiServiceException(UpstreamFailure.Unavailable, "The AI service could not be reached.", inner: e);
        }
    }

    private AiServiceException Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        // Log the status only: upstream bodies may echo user input and are never copied into this API's responses.
        logger.LogWarning("AI service answered HTTP {UpstreamStatus} for {Method} {Path}", status, response.RequestMessage?.Method, response.RequestMessage?.RequestUri?.AbsolutePath);

        return response.StatusCode switch
        {
            HttpStatusCode.ServiceUnavailable => new AiServiceException(UpstreamFailure.Busy, "The AI service is busy.", status, RetryAfterOf(response)),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound =>
                new AiServiceException(UpstreamFailure.ContractMismatch, "The AI service rejected a request this API had already validated.", status),
            _ => new AiServiceException(UpstreamFailure.BadGateway, "The AI service failed.", status),
        };
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }
}
