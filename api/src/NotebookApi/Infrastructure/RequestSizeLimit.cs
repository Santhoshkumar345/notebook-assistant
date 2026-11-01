using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace NotebookApi.Infrastructure;

/// <summary>
/// Refuses an oversized request before reading it. A declared length over the limit is a 413 at once; for a body with no declared length (chunked),
/// the server's own limit is lowered to the same figure so it stops reading when the limit is crossed.
/// </summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next, IOptions<HttpLimitOptions> options)
{
    public async Task Invoke(HttpContext context)
    {
        var limit = options.Value.MaxRequestBodyBytes;

        if (context.Request.ContentLength > limit)
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, $"The request is larger than the {limit / 1024} KiB this service accepts.");
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = limit;

        await next(context);
    }
}
