using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotebookApi.Clients;
using NotebookApi.Data;
using NotebookApi.Infrastructure;

namespace NotebookApi.Services;

public sealed record ReconcileSummary(int UsersChecked, int DocumentsRepaired);

/// <summary>
/// Keeps the AI service's index in step with the database without anyone asking. The database is the source of truth and the index is derived,
/// but on a host whose disk does not last (most free ones), or after the AI service was reinstalled, the index can vanish while every document
/// still says "Indexed": questions would all come back "not found" and nothing would say why. Every few minutes this compares each person's index
/// with their stored documents and re-sends what is missing. It only adds: removing what the database does not know about is left to the explicit sync.
/// </summary>
public sealed class IndexReconciler(IServiceScopeFactory scopes, IOptions<ReconciliationOptions> options, ILogger<IndexReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.StartupDelaySeconds), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One pass over everyone who has documents. Public so a test can run it directly instead of waiting for the timer.</summary>
    public async Task<ReconcileSummary> RunOnceAsync(CancellationToken ct)
    {
        List<Guid> users;
        using (var scope = scopes.CreateScope())
        {
            // The one deliberate bypass of the per-user query filter: this has to find *everyone's* documents. What it does for each person afterwards
            // runs as that person (AmbientUser), through the same filtered code as their own requests.
            users = await scope.ServiceProvider.GetRequiredService<NotebookDb>().Documents.IgnoreQueryFilters().Select(d => d.UserId).Distinct().ToListAsync(ct);
        }

        int checkedUsers = 0, repaired = 0;
        foreach (var user in users)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var asThatUser = AmbientUser.Begin(user);
                using var scope = scopes.CreateScope();
                repaired += await scope.ServiceProvider.GetRequiredService<DocumentService>().ReconcileAsync(ct);
                checkedUsers++;
            }
            catch (AiServiceException failure)
            {
                // Down, slow or busy: the same would hold for everyone else, so stop this pass and try again at the next tick.
                logger.LogWarning("Index reconciliation paused: the AI service is not usable ({Failure}). It will try again.", failure.Failure);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Index reconciliation failed for one person; carrying on with the rest.");
            }
        }

        if (repaired > 0)
            logger.LogInformation("Re-sent {Count} documents that the AI service was missing.", repaired);
        return new ReconcileSummary(checkedUsers, repaired);
    }
}
