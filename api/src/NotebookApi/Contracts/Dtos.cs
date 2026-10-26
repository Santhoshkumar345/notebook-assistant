using System.ComponentModel.DataAnnotations;

namespace NotebookApi.Contracts;

// Property names serialize in camelCase and match the AI service's JSON field names one to one
// (name, content, question, k, mode, status, answer, sources, action, trace, id, source, heading, snippet).

public sealed class DocumentIn
{
    [Required, RegularExpression(Limits.NamePattern, ErrorMessage = "The name must be a markdown file name such as notes.md.")]
    public required string Name { get; init; }

    [Required, StringLength(Limits.MaxDocumentChars, MinimumLength = 1)]
    public required string Content { get; init; }
}

public sealed record DocumentOut(string Name, int Chunks);

public sealed class AskIn
{
    [Required, StringLength(Limits.MaxQuestionChars, MinimumLength = Limits.MinQuestionChars)]
    public required string Question { get; init; }

    [Range(Limits.MinK, Limits.MaxK)]
    public int K { get; init; } = Limits.DefaultK;

    [RegularExpression(Limits.ModePattern, ErrorMessage = "The mode must be 'pipeline' or 'agent'.")]
    public string Mode { get; init; } = "pipeline";
}

public sealed record SourceOut(string Id, string Source, string Heading, string Snippet);

/// <summary>For status "clarify" the answer field holds the clarifying question to put to the user.</summary>
public sealed record AskOut(string Status, string Answer, IReadOnlyList<SourceOut> Sources, string Action, IReadOnlyList<string> Trace);

public sealed record AiHealth(string Status, int Documents, int Chunks);
