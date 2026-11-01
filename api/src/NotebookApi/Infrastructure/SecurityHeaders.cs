namespace NotebookApi.Infrastructure;

/// <summary>
/// Browser-side hardening on every response. The policy is strict because the app is simple: its own scripts and styles, its own API, nothing from
/// anywhere else. 'unsafe-inline' is allowed for styles only, because Angular writes component styles into the page; scripts get no such allowance, so
/// an injected script has nowhere to run even if some text were ever rendered as HTML.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task Invoke(HttpContext context)
    {
        // OnStarting, not set up front: the exception handler clears headers before it writes an error, and these must be on error responses too.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY"; // older browsers; frame-ancestors does the same in current ones
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // What the API returns is one person's private data: nothing in between should keep a copy.
            if (context.Request.Path.StartsWithSegments("/api"))
                headers.CacheControl = "no-store";

            return Task.CompletedTask;
        });

        return next(context);
    }
}
