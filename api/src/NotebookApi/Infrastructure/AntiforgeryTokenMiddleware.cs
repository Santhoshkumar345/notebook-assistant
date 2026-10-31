using Microsoft.AspNetCore.Antiforgery;

namespace NotebookApi.Infrastructure;

/// <summary>
/// CSRF protection for a cookie-authenticated API, in the shape Angular expects: the server hands the page a token in a cookie the page's
/// script can read (XSRF-TOKEN), Angular copies it into the X-XSRF-TOKEN header of every unsafe request, and the server checks the header
/// against the antiforgery cookie, which script cannot read. A page on another site can make the browser send our session cookie but
/// cannot read the token, so it cannot make a request we accept. SameSite=Strict on the session cookie is a second, independent layer.
///
/// Two middleware, because they belong at different points in the pipeline:
///   issuing   runs after authentication (the token is bound to who is signed in) but before authorization, so that the 401 a signed-out
///             page gets from GET /api/auth/me still carries a token for its next call, the sign-in;
///   checking  runs after authorization, so an anonymous request to a protected route is a 401, not a complaint about a token.
/// </summary>
public static class Csrf
{
    public const string CookieName = "XSRF-TOKEN";
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string IssuingPath = "/api/auth/me";

    /// <summary>
    /// Hands the page a token for whoever this request now is. Called wherever that changes (registering, signing in, signing out) so the page
    /// never keeps a token for the previous identity, which the server would refuse: found by driving the real page, where a session that
    /// ended behind its back left the sign-in form unable to submit until a reload.
    /// </summary>
    public static void Issue(HttpContext context)
    {
        var tokens = context.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        context.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // the page's script must read it to send it back
            SameSite = SameSiteMode.Strict,
            Secure = !context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            Path = "/",
        });
    }
}

/// <summary>Sets the XSRF-TOKEN cookie (and the antiforgery cookie it pairs with) on GET /api/auth/me, for the identity making that request.</summary>
public sealed class XsrfTokenIssuerMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals(Csrf.IssuingPath, StringComparison.OrdinalIgnoreCase))
            Csrf.Issue(context);

        await next(context);
    }
}

/// <summary>Refuses an unsafe request (anything but GET, HEAD, OPTIONS, TRACE) whose X-XSRF-TOKEN header does not match its antiforgery cookie.</summary>
public sealed class AntiforgeryValidationMiddleware(RequestDelegate next, IAntiforgery antiforgery)
{
    public async Task Invoke(HttpContext context)
    {
        if (!IsSafe(context.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                await ProblemResponses.WriteAsync(context, StatusCodes.Status400BadRequest, "The request token is missing or invalid. Reload the page and try again.");
                return;
            }
        }

        await next(context);
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
