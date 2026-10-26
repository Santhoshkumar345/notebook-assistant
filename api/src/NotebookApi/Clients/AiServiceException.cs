namespace NotebookApi.Clients;

public enum UpstreamFailure
{
    /// <summary>The AI service could not be reached (connection refused, DNS, reset).</summary>
    Unavailable,

    /// <summary>The AI service did not answer within the configured timeout.</summary>
    Timeout,

    /// <summary>The AI service said it is busy or its model is rate-limited (HTTP 503).</summary>
    Busy,

    /// <summary>The AI service failed (HTTP 502, other 5xx) or returned something unreadable.</summary>
    BadGateway,

    /// <summary>The AI service rejected a request this API had already validated: the two contracts have drifted apart. A bug on our side.</summary>
    ContractMismatch,
}

/// <summary>A failure talking to the AI service, already classified so the HTTP layer can translate it consistently.</summary>
public sealed class AiServiceException(UpstreamFailure failure, string message, int? upstreamStatus = null, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public UpstreamFailure Failure { get; } = failure;
    public int? UpstreamStatus { get; } = upstreamStatus;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
