using System.Text.RegularExpressions;

namespace NotebookApi.Contracts;

/// <summary>
/// Validation rules mirrored from the AI service's contract (ai-service/app/api.py). The API validates at the edge so a bad
/// request fails fast with a clear error before it costs a model call. If the AI service changes a limit, change it here too.
/// </summary>
public static partial class Limits
{
    public const int MaxDocumentChars = 200_000;
    public const int MinQuestionChars = 3;
    public const int MaxQuestionChars = 1000;
    public const int MinK = 1;
    public const int MaxK = 10;
    public const int DefaultK = 4;

    /// <summary>
    /// How much of the conversation is sent with a follow-up so the AI service can rewrite it into a standalone question: the latest six turns (three
    /// exchanges), each cut to 600 characters. The AI service accepts up to eight turns of 2,000, so this is comfortably inside its contract.
    /// </summary>
    public const int MaxHistoryTurns = 6;
    public const int MaxHistoryTurnChars = 600;

    /// <summary>What an earlier answer that found nothing is described as in the history: an empty turn is refused upstream, and silence would read as the person's.</summary>
    public const string NoAnswerText = "(no answer was found)";

    // \z, not $: in .NET, $ also matches just before a trailing newline, so "notes.md\n" would pass a $ pattern here while the
    // AI service's stricter pattern rejects it, and this API would report its own validation gap as a 502 from upstream.
    public const string NamePattern = @"^[A-Za-z0-9][A-Za-z0-9._ -]{0,98}\.md\z";
    public const string ModePattern = @"^(pipeline|agent)\z";

    [GeneratedRegex(NamePattern)]
    public static partial Regex DocumentName();
}
