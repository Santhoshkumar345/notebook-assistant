using System.ComponentModel.DataAnnotations;

namespace NotebookApi.Infrastructure;

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// When set, registering requires this code. Open registration on a public deployment hands anyone your model quota, so set it
    /// before exposing the API. Provide it through user secrets or the environment (Auth__InviteCode), never a committed file.
    /// </summary>
    [MinLength(8)]
    public string? InviteCode { get; set; }

    /// <summary>
    /// Outside Development the app refuses to start unless there is an invite code or this is true: open registration has to be a decision, not a default.
    /// </summary>
    public bool OpenRegistration { get; set; }
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Sign-in and registration attempts per minute from one address.</summary>
    [Range(1, 100_000)]
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Questions per minute from one user: each can cost several model calls.</summary>
    [Range(1, 100_000)]
    public int AskPerMinute { get; set; } = 20;
}
