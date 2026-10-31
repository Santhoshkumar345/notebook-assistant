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

    /// <summary>
    /// True when this question is the person's answer to a clarifying question the conversation is waiting on. The AI service is
    /// then given the original question together with the answer (and any earlier answers in the same exchange). Requires
    /// conversationId; 409 if the conversation is not waiting for an answer. What is saved is what the person typed.
    /// </summary>
    public bool AnswersClarification { get; init; }

    /// <summary>The same request with a different question text (the combined one), everything else unchanged.</summary>
    public AskIn WithQuestion(string question) => new() { Question = question, K = K, Mode = Mode, ConversationId = ConversationId, AnswersClarification = AnswersClarification };
}

public sealed record SourceOut(string Id, string Source, string Heading, string Snippet);

/// <summary>The AI service's reply, as it sends it. For status "clarify" the answer field holds the clarifying question to put to the user.</summary>
public sealed record AskOut(AnswerStatus Status, string Answer, IReadOnlyList<SourceOut> Sources, string Action, IReadOnlyList<string> Trace);

public sealed record AiHealth(string Status);

/// <summary>Status is "ok", or "degraded" (HTTP 503) when the AI service cannot be reached, in which case Ai is null. Public and anonymous, so it carries no counts: they would span every user.</summary>
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
public sealed record AskResponse(Guid ConversationId, int MessageId, AnswerStatus Status, string Answer, IReadOnlyList<SourceOut> Sources, string Action, IReadOnlyList<string> Trace);

public sealed record ConversationSummary(Guid Id, string Title, int Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MessageOut(int Id, MessageRole Role, string Text, AnswerStatus? Status, string? Action, IReadOnlyList<SourceOut> Sources, DateTimeOffset CreatedAt);

public sealed record ConversationOut(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<MessageOut> Messages);

// ----------------------------------------------------------------- accounts

public sealed class RegisterIn
{
    [Required, EmailAddress, StringLength(254)]
    public required string Email { get; init; }

    /// <summary>At least 12 characters and no composition rules (length matters, "must contain a symbol" does not); at most 128, so a huge value cannot be used to make the hasher work for ages.</summary>
    [Required, StringLength(128, MinimumLength = 12)]
    public required string Password { get; init; }

    /// <summary>Needed only when the server is configured with an invite code.</summary>
    [StringLength(200)]
    public string? InviteCode { get; init; }
}

public sealed class LoginIn
{
    [Required, StringLength(254)]
    public required string Email { get; init; }

    [Required, StringLength(128, MinimumLength = 1)]
    public required string Password { get; init; }
}

public sealed record UserInfo(Guid Id, string Email);
