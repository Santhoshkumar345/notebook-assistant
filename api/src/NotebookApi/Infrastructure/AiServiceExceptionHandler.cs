using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NotebookApi.Clients;

namespace NotebookApi.Infrastructure;

/// <summary>
/// Turns a classified AI-service failure into an RFC 7807 problem response. What the caller sees:
///   busy        -> 503 with Retry-After (the upstream's value, else 10 s)
///   unavailable -> 503 with Retry-After 5
///   timeout     -> 504
///   anything else the upstream did wrong, including a contract mismatch -> 502
/// Upstream bodies and exception messages are never copied into the response.
/// </summary>
public sealed class AiServiceExceptionHandler(ILogger<AiServiceExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not AiServiceException failure)
            return false;

        var (status, title, retryAfter) = failure.Failure switch
        {
            UpstreamFailure.Busy => (503, "The AI service is busy. Please retry shortly.", failure.RetryAfter ?? TimeSpan.FromSeconds(10)),
            UpstreamFailure.Unavailable => (503, "The AI service is unavailable.", (TimeSpan?)TimeSpan.FromSeconds(5)),
            UpstreamFailure.Timeout => (504, "The AI service did not respond in time.", (TimeSpan?)null),
            _ => (502, "The AI service returned an unexpected response.", (TimeSpan?)null),
        };

        if (failure.Failure == UpstreamFailure.ContractMismatch)
            logger.LogError(failure, "Contract mismatch with the AI service (upstream HTTP {UpstreamStatus}): the API and the AI service disagree about a request", failure.UpstreamStatus);
        else
            logger.LogWarning("AI service failure: {Failure} (upstream HTTP {UpstreamStatus})", failure.Failure, failure.UpstreamStatus);

        context.Response.StatusCode = status;
        if (retryAfter is { } wait)
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString();

        var problem = new ProblemDetails { Status = status, Title = title };
        if (context.Items[CorrelationIdMiddleware.Header] is string requestId)
            problem.Extensions["requestId"] = requestId;

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
