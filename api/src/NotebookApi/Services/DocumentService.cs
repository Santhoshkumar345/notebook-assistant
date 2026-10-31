using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NotebookApi.Clients;
using NotebookApi.Contracts;
using NotebookApi.Data;
using NotebookApi.Infrastructure;

namespace NotebookApi.Services;

/// <summary>
/// One writer at a time per user for document changes. The database and the AI service cannot share a transaction, so two overlapping
/// uploads of the same name could leave one version in the database and the other in the index. Documents are added rarely and
/// each takes seconds, so a single gate is simpler and safer than finer locking. It protects one running instance only; a second
/// instance would need a database-level lock.
/// </summary>
public sealed class DocumentWriteGate
{
    // One gate per user: a slow upload must not make other people wait. (One semaphore is kept for every user who has ever written.)
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    public async Task<IDisposable> EnterAsync(Guid user, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

/// <summary>
/// Keeps the database (source of truth) and the AI service's index in step. Every write goes to the database first, is marked
/// Pending, and is confirmed only once the AI service accepts it. A failure leaves a Failed row that can be re-indexed, never a
/// document that exists in one place and silently not in the other.
/// </summary>
public sealed class DocumentService(NotebookDb db, IAiServiceClient ai, TimeProvider clock, DocumentWriteGate gate, ICurrentUser currentUser)
{
    private Guid UserId => currentUser.Id ?? throw new InvalidOperationException("Documents can only be changed by a signed-in user.");

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(CancellationToken ct)
    {
        // Content.Length is computed by the database, so the (up to 200,000-character) text is not loaded for a list.
        var rows = await db.Documents.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new { d.Name, Chars = d.Content.Length, d.Chunks, d.Status, d.Error, d.CreatedAt, d.UpdatedAt })
            .ToListAsync(ct);
        return rows.Select(r => new DocumentInfo(r.Name, r.Chars, r.Chunks, r.Status, r.Error, r.CreatedAt, r.UpdatedAt)).ToList();
    }

    /// <summary>Adds a document, or replaces the one with the same name. Throws <see cref="AiServiceException"/> if the index could not be updated; the row is then Failed.</summary>
    public async Task<(DocumentInfo Document, bool Created)> UpsertAsync(DocumentIn input, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(UserId, ct);

        var row = await db.Documents.SingleOrDefaultAsync(d => d.Name == input.Name, ct);
        var created = row is null;
        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new StoredDocument { Name = input.Name, Content = input.Content, CreatedAt = now };
            db.Documents.Add(row);
        }

        row.Content = input.Content;
        row.Status = DocumentStatus.Pending;
        row.Chunks = null;
        row.Error = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await IndexAsync(row, ct);
        return (ToInfo(row), created);
    }

    /// <summary>Re-sends one stored document to the AI service. Null if there is no such document.</summary>
    public async Task<DocumentInfo?> ReindexAsync(string name, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(UserId, ct);

        var row = await db.Documents.SingleOrDefaultAsync(d => d.Name == name, ct);
        if (row is null) return null;
        await IndexAsync(row, ct);
        return ToInfo(row);
    }

    /// <summary>Removes the document from the index first, then from the database, so a failure never leaves a row that claims a document the index no longer has (or the reverse). Safe to repeat.</summary>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(UserId, ct);

        var row = await db.Documents.SingleOrDefaultAsync(d => d.Name == name, ct);
        if (row is null) return false;

        await ai.DeleteDocumentAsync(name, ct); // false (already absent upstream) is fine: the goal is that it is gone
        db.Documents.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Makes the AI service match the database: deletes what it holds that the database does not, then re-sends every stored document.
    /// Stops at the first upstream failure (that document is marked Failed); running it again continues, because every step is idempotent.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(UserId, ct);

        var upstream = await ai.GetDocumentsAsync(ct); // first, so an unreachable service fails before anything is changed
        var rows = await db.Documents.OrderBy(d => d.Name).ToListAsync(ct);
        var known = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        var removed = new List<string>();
        foreach (var orphan in upstream.Keys.Where(n => !known.Contains(n)).Order(StringComparer.Ordinal))
        {
            await ai.DeleteDocumentAsync(orphan, ct);
            removed.Add(orphan);
        }

        foreach (var row in rows)
            await IndexAsync(row, ct);

        return new SyncResult(rows.Count, removed);
    }

    private async Task IndexAsync(StoredDocument row, CancellationToken ct)
    {
        row.Status = DocumentStatus.Pending;
        try
        {
            var result = await ai.AddDocumentAsync(new DocumentIn { Name = row.Name, Content = row.Content }, ct);
            row.Status = DocumentStatus.Indexed;
            row.Chunks = result.Chunks;
            row.Error = null;
        }
        catch (Exception ex) when (ex is AiServiceException or OperationCanceledException)
        {
            // Recorded even when the caller has gone away (cancelled), otherwise the row would stay Pending forever.
            row.Status = DocumentStatus.Failed;
            row.Error = ex is AiServiceException failure ? failure.Failure.ToString() : "Cancelled";
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static DocumentInfo ToInfo(StoredDocument d) =>
        new(d.Name, d.Content.Length, d.Chunks, d.Status, d.Error, d.CreatedAt, d.UpdatedAt);
}
