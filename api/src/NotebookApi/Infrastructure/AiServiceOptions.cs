using System.ComponentModel.DataAnnotations;

namespace NotebookApi.Infrastructure;

public sealed class AiServiceOptions
{
    public const string Section = "AiService";

    /// <summary>Where the Python AI service listens. It is an internal service: browsers never call it directly.</summary>
    [Required, Url]
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>
    /// How long to wait for the AI service. A question can legitimately take a while: the service retries rate-limited model
    /// calls with backoff, and the agent mode makes several model calls in a row.
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 60;
}
