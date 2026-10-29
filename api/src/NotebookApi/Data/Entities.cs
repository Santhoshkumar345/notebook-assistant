namespace NotebookApi.Data;

/// <summary>
/// Pending: being sent to the AI service. Indexed: the AI service confirmed it. Failed: the last attempt did not
/// succeed, so the search index may be missing this document or still hold an older version of it.
/// </summary>
public enum DocumentStatus { Pending, Indexed, Failed }

public enum MessageRole { User, Assistant }

/// <summary>
/// The source of truth for an uploaded document. The AI service's vector index is derived from this and can be rebuilt
/// from it (see DocumentService.SyncAsync), which is why the full text is kept here.
/// </summary>
public sealed class StoredDocument
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Content { get; set; }
    public DocumentStatus Status { get; set; }
    public int? Chunks { get; set; }

    /// <summary>The kind of failure (for example "Timeout"), never an upstream message or body.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>One turn. User turns hold the question; assistant turns hold the answer (or the clarifying question) and what it cited.</summary>
public sealed class ChatMessage
{
    public int Id { get; set; }
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public required string Text { get; set; }

    /// <summary>Assistant turns only: answered, not_found, uncited or clarify.</summary>
    public string? Status { get; set; }

    public string? Action { get; set; }

    /// <summary>The cited sources as JSON (an array of SourceOut), kept as text so the history survives later changes to the document store.</summary>
    public string? SourcesJson { get; set; }

    /// <summary>The correlation id of the request that produced this turn, to find its log lines.</summary>
    public string? RequestId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
