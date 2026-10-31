using System.ComponentModel.DataAnnotations;
using NotebookApi.Data;

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

    /// <summary>Continue this conversation. Omitted: a new one is started. Used only to save the exchange; the AI service never sees it.</summary>
    public Guid? ConversationId { get; init; }
}

public sealed record SourceOut(string Id, string Source, string Heading, string Snippet);

/// <summary>The AI service's reply, as it sends it. For status "clarify" the answer field holds the clarifying question to put to the user.</summary>
public sealed record AskOut(string Status, string Answer, IReadOnlyList<SourceOut> Sources, string Action, IReadOnlyList<string> Trace);

public sealed record AiHealth(string Status, int Documents, int Chunks);

/// <summary>Status is "ok", or "degraded" (HTTP 503) when the AI service cannot be reached, in which case Ai is null.</summary>
public sealed record HealthOut(string Status, AiHealth? Ai);

/// <summary>Media type of every error body (RFC 7807). Declared on the controllers so the OpenAPI document, and the types generated from it, describe the errors too.</summary>
public static class ProblemContent
{
    public const string Type = "application/problem+json";
}

// ----------------------------------------------------------------- what the API itself returns (persisted state)

/// <summary>A document as stored here. Status is Pending, Indexed or Failed; see DocumentStatus.</summary>
public sealed record DocumentInfo(string Name, int Chars, int? Chunks, DocumentStatus Status, string? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>What a sync did: how many documents were re-sent to the AI service, and which names it held that this API did not know about (now removed).</summary>
public sealed record SyncResult(int Reindexed, IReadOnlyList<string> RemovedFromIndex);

/// <summary>The AI service's answer, plus where it was saved.</summary>
public sealed record AskResponse(Guid ConversationId, int MessageId, string Status, string Answer, IReadOnlyList<SourceOut> Sources, string Action, IReadOnlyList<string> Trace);

public sealed record ConversationSummary(Guid Id, string Title, int Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MessageOut(int Id, string Role, string Text, string? Status, string? Action, IReadOnlyList<SourceOut> Sources, DateTimeOffset CreatedAt);

public sealed record ConversationOut(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<MessageOut> Messages);
