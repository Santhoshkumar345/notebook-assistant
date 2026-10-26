using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NotebookApi.Infrastructure;

/// <summary>
/// One id per user action, carried through every service that handles it: read from X-Request-ID (or generated), returned on the
/// response, forwarded to the AI service, and attached to every log line. An incoming value is accepted only if it is short and plain,
/// so a caller cannot smuggle newlines or control characters into the logs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Request-ID";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]  // \z: $ would also accept a trailing newline
    private static partial Regex Valid();

    public async Task Invoke(HttpContext context)
    {
        var incoming = context.Request.Headers[Header].FirstOrDefault();
        var id = incoming is not null && Valid().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");
        context.Items[Header] = id;

        // OnStarting, not a plain assignment: the exception handler clears the response (headers included) before it writes
        // an error, so a header set up front would be lost on exactly the responses where the id matters most.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        // A template scope, not a dictionary: the console logger prints a scope with ToString(), and a dictionary prints as its type name.
        // Named CorrelationId because ASP.NET already adds its own RequestId scope (a per-connection id, not ours).
        using (logger.BeginScope("CorrelationId:{CorrelationId}", id))
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                // Path only, never the query string or a body: nothing a user typed is written to the logs.
                logger.LogInformation("{Method} {Path} -> {Status} in {Ms} ms", context.Request.Method, context.Request.Path.Value, context.Response.StatusCode, watch.ElapsedMilliseconds);
            }
        }
    }
}

/// <summary>Adds the current request's correlation id to calls made to the AI service.</summary>
public sealed class CorrelationIdHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext?.Items[CorrelationIdMiddleware.Header] is string id)
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.Header, id);
        return base.SendAsync(request, cancellationToken);
    }
}
