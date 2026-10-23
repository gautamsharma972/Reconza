using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

/// <summary>Wakes workers as soon as a run is queued instead of waiting for the next poll.</summary>
public sealed class RunQueueSignal
{
    private readonly SemaphoreSlim _signal = new(0);
    public void Notify() => _signal.Release();
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _signal.WaitAsync(timeout, ct);
}

public sealed class WorkerOptions
{
    public int Concurrency { get; set; } = 2;
    public int PollSeconds { get; set; } = 5;
    /// <summary>A Processing run whose heartbeat is older than this is assumed orphaned (crash, deploy) and retried.</summary>
    public int StaleAfterSeconds { get; set; } = 180;
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// Durable, database-backed run queue. Runs survive restarts; claiming is an atomic conditional UPDATE,
/// so several workers (or several app instances) never process the same run twice.
/// </summary>
public sealed class RunWorker(IServiceScopeFactory scopes, RunQueueSignal signal, WorkerOptions options, ILogger<RunWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, options.Concurrency)).Select(i => LoopAsync(i, stoppingToken)).Append(RecoverLoopAsync(stoppingToken)));

    private async Task LoopAsync(int worker, CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await TryProcessNextAsync(ct)) continue; // drain the queue before sleeping
                await signal.WaitAsync(TimeSpan.FromSeconds(options.PollSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker {Worker} loop error", worker);
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken ct)
    {
        Guid runId, tenantId;
        string createdBy;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var next = await db.Runs.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Status == RunStatus.Queued)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new { r.Id, r.TenantId, r.CreatedBy })
                .FirstOrDefaultAsync(ct);
            if (next is null) return false;

            var now = DateTime.UtcNow;
            var claimed = await db.Runs.IgnoreQueryFilters()
                .Where(r => r.Id == next.Id && r.Status == RunStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatus.Processing)
                    .SetProperty(r => r.Stage, "Starting")
                    .SetProperty(r => r.Progress, 5)
                    .SetProperty(r => r.StartedAt, now)
                    .SetProperty(r => r.HeartbeatAt, now)
                    .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct);
            if (claimed == 0) return true; // another worker won; look again
            (runId, tenantId, createdBy) = (next.Id, next.TenantId, next.CreatedBy);
        }

        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = HeartbeatAsync(runId, heartbeatStop.Token);
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId, createdBy);
            log.LogInformation("Processing run {RunId}", runId);
            await scope.ServiceProvider.GetRequiredService<RunService>().ProcessAsync(runId, ct);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
        }
        return true;
    }

    /// <summary>Keeps the claim alive during long steps (e.g. writing a very large workbook).</summary>
    private async Task HeartbeatAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.StaleAfterSeconds / 4)));
            while (await timer.WaitForNextTickAsync(ct))
            {
                using var scope = scopes.CreateScope();
                var now = DateTime.UtcNow;
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Runs.IgnoreQueryFilters()
                    .Where(r => r.Id == runId && r.Status == RunStatus.Processing)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.HeartbeatAt, now), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log.LogWarning(ex, "Heartbeat for run {RunId} stopped", runId); }
    }

    private async Task RecoverLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var cutoff = DateTime.UtcNow.AddSeconds(-options.StaleAfterSeconds);
                var requeued = await db.Runs.IgnoreQueryFilters()
                    .Where(r => r.Status == RunStatus.Processing && r.HeartbeatAt < cutoff && r.Attempts < options.MaxAttempts)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Queued).SetProperty(r => r.Stage, "Retrying after interruption"), ct);
                var failed = await db.Runs.IgnoreQueryFilters()
                    .Where(r => r.Status == RunStatus.Processing && r.HeartbeatAt < cutoff && r.Attempts >= options.MaxAttempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatus.Failed)
                        .SetProperty(r => r.Stage, "Failed")
                        .SetProperty(r => r.Error, "Processing was interrupted repeatedly. Please run it again or contact support."), ct);
                if (requeued + failed > 0)
                {
                    log.LogWarning("Recovered stale runs: {Requeued} re-queued, {Failed} failed", requeued, failed);
                    if (requeued > 0) signal.Notify();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Run recovery failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
