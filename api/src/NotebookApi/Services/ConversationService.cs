using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotebookApi.Contracts;
using NotebookApi.Data;

namespace NotebookApi.Services;

/// <summary>
/// Chat history. A conversation is a record of what was asked and answered, not context given to the model: the AI service
/// takes one question at a time, so a follow-up such as "and the second one?" is not understood from earlier turns.
/// </summary>
public sealed class ConversationService(NotebookDb db, TimeProvider clock)
{
    public const int MaxTitleChars = 80;
    public const int MaxListLimit = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => db.Conversations.AnyAsync(c => c.Id == id, ct);

    /// <summary>
    /// Saves a finished question and answer together, or nothing. Failed asks are not history: the caller sees the error and retries.
    /// Returns null if the conversation named by <paramref name="conversationId"/> no longer exists (for example it was deleted while the model was answering).
    /// </summary>
    public async Task<AskResponse?> SaveExchangeAsync(Guid? conversationId, AskIn question, AskOut answer, string? requestId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        Conversation conversation;
        if (conversationId is { } id)
        {
            var existing = await db.Conversations.FindAsync([id], ct);
            if (existing is null) return null;
            conversation = existing;
        }
        else
        {
            conversation = new Conversation { Id = Guid.NewGuid(), Title = MakeTitle(question.Question), CreatedAt = now, UpdatedAt = now };
            db.Conversations.Add(conversation);
        }

        var asked = new ChatMessage { ConversationId = conversation.Id, Role = MessageRole.User, Text = question.Question, RequestId = requestId, CreatedAt = now };
        var replied = new ChatMessage
        {
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Text = answer.Answer,
            Status = answer.Status,
            Action = answer.Action,
            SourcesJson = JsonSerializer.Serialize(answer.Sources, Json),
            RequestId = requestId,
            CreatedAt = now,
        };
        db.Messages.AddRange(asked, replied); // added in this order, so the user turn gets the lower id
        conversation.UpdatedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (conversationId is not null)
        {
            return null; // the conversation was deleted between the lookup and the save (foreign key)
        }

        return new AskResponse(conversation.Id, replied.Id, answer.Status, answer.Answer, answer.Sources, answer.Action, answer.Trace);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(int limit, CancellationToken ct) =>
        await db.Conversations.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.CreatedAt)
            .Take(Math.Clamp(limit, 1, MaxListLimit))
            .Select(c => new ConversationSummary(c.Id, c.Title, c.Messages.Count, c.CreatedAt, c.UpdatedAt))
            .ToListAsync(ct);

    public async Task<ConversationOut?> GetAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking().Include(c => c.Messages.OrderBy(m => m.Id)).SingleOrDefaultAsync(c => c.Id == id, ct);
        return c is null
            ? null
            : new ConversationOut(c.Id, c.Title, c.CreatedAt, c.UpdatedAt, c.Messages.Select(ToOut).ToList());
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct) =>
        await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0; // the messages go with it (cascade)

    /// <summary>The first question, tidied: whitespace collapsed, cut to a length that fits a list.</summary>
    public static string MakeTitle(string question)
    {
        var tidy = string.Join(' ', question.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return tidy.Length <= MaxTitleChars ? tidy : tidy[..(MaxTitleChars - 1)].TrimEnd() + "…";
    }

    private static MessageOut ToOut(ChatMessage m) =>
        new(m.Id, m.Role.ToString().ToLowerInvariant(), m.Text, m.Status, m.Action,
            m.SourcesJson is null ? [] : JsonSerializer.Deserialize<List<SourceOut>>(m.SourcesJson, Json) ?? [], m.CreatedAt);
}
