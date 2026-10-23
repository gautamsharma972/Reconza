using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

/// <summary>Enforces each tenant's retention policy by deleting uploaded files and reports older than it.</summary>
public sealed class RetentionService(IServiceScopeFactory scopes, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Retention sweep failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        List<Tenant> tenants;
        using (var scope = scopes.CreateScope())
            tenants = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants.AsNoTracking().ToListAsync(ct);

        foreach (var t in tenants)
        {
            using var scope = scopes.CreateScope();
            ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Use(t.Id, "retention-policy");
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var runs = scope.ServiceProvider.GetRequiredService<RunService>();
            var cutoff = DateTime.UtcNow.AddDays(-t.RetentionDays);
            var expired = await db.Runs.Include(r => r.Files).Where(r => r.CreatedAt < cutoff && r.FilesPurgedAt == null).ToListAsync(ct);
            foreach (var run in expired) await runs.PurgeFilesAsync(run, $"Retention policy ({t.RetentionDays} days).", ct);
            if (expired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                log.LogInformation("Purged files for {Count} runs of tenant {Tenant}", expired.Count, t.Id);
            }
        }
    }
}
