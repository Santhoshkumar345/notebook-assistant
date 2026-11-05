using System.Text.RegularExpressions;

namespace NotebookApi.Infrastructure;

/// <summary>
/// Serves the built Angular app from the API itself, so in production the browser has one origin (no proxy, no CORS) and there is one container fewer.
/// The build goes in the web root (ASPNETCORE_WEBROOT, or wwwroot). With no build there, only the API answers, which is how development works.
/// </summary>
public static partial class SpaHosting
{
    // Angular names its scripts, styles and fonts after their content (main-J5K3L2M1.js, inter-latin-wght-normal-NRMW37G5.woff2), so a changed file always has a new name
    // and may be cached for good. A file kept under its own name (theme-init.js, favicon.svg) is not content-named and is always re-checked.
    [GeneratedRegex(@"-[A-Z0-9]{8}\.(js|css|woff2)$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentHashed();

    /// <summary>The built app's files. Early in the pipeline: they need no sign-in, and a script or stylesheet should never be refused for lack of one.</summary>
    public static void UseSpaFiles(this WebApplication app)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
                context.Context.Response.Headers.CacheControl = ContentHashed().IsMatch(context.File.Name) ? "public, max-age=31536000, immutable" : "no-cache",
        });
    }

    /// <summary>Everything else a browser might ask for. Mapped with the other endpoints, so a real route always wins over it.</summary>
    public static void MapSpaFallback(this WebApplication app)
    {
        // Any other address a browser might ask for (/chat, /chat/<id>, /documents) is the app's own route: hand back the app and let it decide.
        // An /api address that matched nothing is an error for a program to read, not a web page; a missing file (it has an extension) is a 404.
        // An explicit pattern: the default one skips anything with a file extension, which would leave a missing /main-X.js to the fallback
        // authorization policy (a 401 for a stranger) instead of this handler's honest 404.
        app.MapFallback("{*path}", async context =>
        {
            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            var isPage = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                         && !context.Request.Path.StartsWithSegments("/api")
                         && !Path.HasExtension(context.Request.Path.Value)
                         && index.Exists;

            if (!isPage)
            {
                await ProblemResponses.WriteAsync(context, StatusCodes.Status404NotFound, "Not found.");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache"; // always ask: it names the current scripts
            await context.Response.SendFileAsync(index);
        }).AllowAnonymous();
    }
}
