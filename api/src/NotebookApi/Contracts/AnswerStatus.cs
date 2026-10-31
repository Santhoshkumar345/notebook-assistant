using System.Text.Json.Serialization;

namespace NotebookApi.Contracts;

/// <summary>
/// How an ask ended. The names on the wire are the AI service's (answered, not_found, uncited, clarify); declaring them here makes
/// the OpenAPI document list the four values, so the generated TypeScript is a union the page's code is checked against. A value
/// the API does not know fails to parse, which the AI-service client reports as a bad gateway instead of passing it on.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnswerStatus>))]
public enum AnswerStatus
{
    /// <summary>An answer, with sources that were verified to exist.</summary>
    [JsonStringEnumMemberName("answered")] Answered,

    /// <summary>The documents do not contain the answer. The answer text is empty.</summary>
    [JsonStringEnumMemberName("not_found")] NotFound,

    /// <summary>The model claimed an answer but cited no valid source, so it is not trusted. The answer text is empty.</summary>
    [JsonStringEnumMemberName("uncited")] Uncited,

    /// <summary>The answer field holds a clarifying question to put to the person.</summary>
    [JsonStringEnumMemberName("clarify")] Clarify,
}

/// <summary>The wire names, for the one place they are stored as text (the database column).</summary>
public static class AnswerStatusWire
{
    public static string ToWire(AnswerStatus status) => status switch
    {
        AnswerStatus.Answered => "answered",
        AnswerStatus.NotFound => "not_found",
        AnswerStatus.Uncited => "uncited",
        AnswerStatus.Clarify => "clarify",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static AnswerStatus? Parse(string? wire) => wire switch
    {
        null => null,
        "answered" => AnswerStatus.Answered,
        "not_found" => AnswerStatus.NotFound,
        "uncited" => AnswerStatus.Uncited,
        "clarify" => AnswerStatus.Clarify,
        _ => throw new InvalidOperationException($"The database holds an unknown answer status '{wire}'."),
    };
}
