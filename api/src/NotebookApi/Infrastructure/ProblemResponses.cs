using Microsoft.AspNetCore.Mvc;

namespace NotebookApi.Infrastructure;

/// <summary>Writes an RFC 7807 error from code that runs outside a controller (middleware, authentication and rate-limit events), in the same shape controllers produce.</summary>
public static class ProblemResponses
{
    public static async Task WriteAsync(HttpContext context, int status, string title, TimeSpan? retryAfter = null)
    {
        context.Response.StatusCode = status;
        if (retryAfter is { } wait)
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString();

        var problem = new ProblemDetails { Status = status, Title = title };
        if (context.Items[CorrelationIdMiddleware.Header] is string requestId)
            problem.Extensions["requestId"] = requestId;

        var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
