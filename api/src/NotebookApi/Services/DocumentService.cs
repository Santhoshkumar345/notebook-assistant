using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
public sealed class DocumentService(NotebookDb db, IAiServiceClient ai, TimeProvider clock, DocumentWriteGate gate, ICurrentUser currentUser, IOptions<QuotaOptions> quotas)
{
    /// <summary>A document still marked Pending after this long was not finished by whatever was uploading it (a crash, a restart): it is sent again.</summary>
    private static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(2);

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
        await EnforceQuotaAsync(row, input, ct); // before anything is saved or sent: a refused upload costs nothing
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

    /// <summary>
    /// Repairs this person's index without being asked: re-sends every stored document the AI service does not have (or holds with a different chunk
    /// count), every Failed one, and every one left Pending by something that stopped. Adds only; deleting what the database does not know about is
    /// left to <see cref="SyncAsync"/>. Returns how many were re-sent. Throws <see cref="AiServiceException"/> when the AI service is down, busy or slow,
    /// because the next document would meet the same wall.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        using var _ = await gate.EnterAsync(UserId, ct);
        var indexed = await ai.GetDocumentsAsync(ct); // inside the gate: an upload that is in flight would otherwise look missing

        var staleBefore = clock.GetUtcNow() - PendingGrace;
        var repaired = 0;
        foreach (var row in await db.Documents.OrderBy(d => d.Name).ToListAsync(ct))
        {
            var needsSending = row.Status switch
            {
                DocumentStatus.Indexed => !indexed.TryGetValue(row.Name, out var chunks) || chunks != row.Chunks,
                DocumentStatus.Failed => true,
                _ => row.UpdatedAt < staleBefore,
            };
            if (!needsSending) continue;

            try
            {
                await IndexAsync(row, ct);
                repaired++;
            }
            catch (AiServiceException failure) when (failure.Failure is UpstreamFailure.BadGateway or UpstreamFailure.ContractMismatch)
            {
                // this document was refused (and is now marked Failed); the AI service itself is fine, so carry on with the others
            }
        }

        return repaired;
    }

    private async Task EnforceQuotaAsync(StoredDocument? existing, DocumentIn input, CancellationToken ct)
    {
        var limits = quotas.Value;
        if (existing is null && await db.Documents.CountAsync(ct) >= limits.MaxDocuments)
            throw new QuotaExceededException($"You have reached the limit of {limits.MaxDocuments} documents. Delete one to add another.");

        var stored = await db.Documents.SumAsync(d => (long)d.Content.Length, ct);
        var after = stored - (existing?.Content.Length ?? 0) + input.Content.Length;
        if (after > limits.MaxTotalDocumentChars)
        {
            throw new QuotaExceededException(
                $"Your documents would hold {after.ToString("N0", CultureInfo.InvariantCulture)} characters, and the limit is {limits.MaxTotalDocumentChars.ToString("N0", CultureInfo.InvariantCulture)}. Delete or shorten a document to make room.");
        }
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
