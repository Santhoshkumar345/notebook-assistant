using Microsoft.AspNetCore.Diagnostics;

namespace NotebookApi.Infrastructure;

/// <summary>A person asked for more than their quota allows. The message is meant for them, so it says what the limit is and what would make room.</summary>
public sealed class QuotaExceededException(string message) : Exception(message);

/// <summary>A quota refusal is a 403 with the reason as the title, which the web app shows as it is.</summary>
public sealed class QuotaExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not QuotaExceededException quota)
            return false;

        await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden, quota.Message);
        return true;
    }
}
