using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SettleFlow.Core.Presets;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public class RunQueueTests
{
    private sealed class MemoryFileStore : IFileStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new();
        public Task<string> SaveAsync(Guid t, string kind, byte[] c, CancellationToken ct = default)
        { var k = $"{t:N}/{kind}/{Guid.NewGuid():N}"; _files[k] = c; return Task.FromResult(k); }
        public Task<byte[]> ReadAsync(Guid t, string key, CancellationToken ct = default) => Task.FromResult(_files[key]);
        public Task DeleteAsync(Guid t, string key, CancellationToken ct = default) { _files.TryRemove(key, out _); return Task.CompletedTask; }
    }

    private sealed class CleanScanner : IMalwareScanner
    {
        public ScanMode Mode => ScanMode.Optional;
        public Task<ScanResult> ScanAsync(byte[] c, CancellationToken ct = default) => Task.FromResult(new ScanResult(ScanVerdict.Clean, "test"));
        public Task<string> HealthAsync(CancellationToken ct = default) => Task.FromResult("Ready");
    }

    private sealed class NullEmail : IEmailSender { public Task SendAsync(EmailMessage m, CancellationToken ct = default) => Task.CompletedTask; }

    // Each DbContext opens its own connection to a temp database file, as in production. (A single shared
    // in-memory SqliteConnection is not thread-safe and made these multi-threaded tests flaky.)
    private static string TempDb() => $"Data Source={Path.Combine(Path.GetTempPath(), $"reconza-queue-{Guid.NewGuid():N}.db")}";

    private static ServiceProvider Services(string connectionString)
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        s.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        s.AddScoped<TenantContext>();
        s.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        s.AddDbContext<AppDbContext>(o => o.UseSqlite(connectionString));
        s.AddSingleton<IFileStore, MemoryFileStore>();
        s.AddSingleton<IMalwareScanner, CleanScanner>();
        s.AddSingleton(new WorkerOptions { Concurrency = 2, PollSeconds = 1, StaleAfterSeconds = 60 });
        s.AddSingleton<RunQueueSignal>();
        s.AddScoped<AuditLog>();
        s.AddScoped<UploadInspector>();
        s.AddScoped<RunService>();
        s.AddSingleton<IEmailSender, NullEmail>();
        s.AddSingleton(new AppUrls(new AppOptions()));
        s.AddSingleton<IHostedService, RunWorker>();
        return s.BuildServiceProvider();
    }

    private static async Task<(Guid Tenant, Workflow Wf)> SeedAsync(ServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var tenantId = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId, "owner@test");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "T", Plan = Plan.Business });
        var wf = new Workflow { Name = "Drivers", PresetKey = "driver-settlement", DefinitionJson = WorkflowJson.Serialize(WorkflowPresets.DriverSettlement()) };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();
        return (tenantId, wf);
    }

    private static async Task<Run> WaitForAsync(ServiceProvider sp, Guid tenant, Guid runId, params RunStatus[] done)
    {
        for (var i = 0; i < 300; i++)
        {
            using var scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant);
            var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
            if (done.Contains(run.Status)) return run;
            await Task.Delay(100);
        }
        throw new TimeoutException("Run did not finish.");
    }

    [Fact]
    public async Task Queued_run_is_processed_in_background()
    {
        await using var sp = Services(TempDb());
        var (tenant, wf) = await SeedAsync(sp);

        Guid runId;
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant, "owner@test");
            var uploads = SampleData.DriverSettlement().Select(f => new UploadedFile(f.SourceName, f.FileName, f.Content)).ToList();
            var outcome = await scope.ServiceProvider.GetRequiredService<RunService>().EnqueueAsync(wf, uploads, "Week 42");
            Assert.Empty(outcome.Errors);
            Assert.Equal(RunStatus.Queued, outcome.Run!.Status);
            runId = outcome.Run.Id;
        }

        var worker = sp.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);
        var run = await WaitForAsync(sp, tenant, runId, RunStatus.Completed, RunStatus.Failed);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(5, run.GroupCount);
        Assert.Equal(9, run.ExceptionCount);
        Assert.Equal(1, run.Attempts);
        Assert.NotNull(run.ReportStorageKey);
        Assert.NotNull(run.DetailsStorageKey);

        using var s2 = sp.CreateScope();
        s2.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant);
        var details = await s2.ServiceProvider.GetRequiredService<RunService>().ReadDetailsAsync(run);
        Assert.Equal(4, details.Count(d => d.Group == "D001" && d.Source == "Trips" && d.Included));
    }

    [Fact]
    public async Task Orphaned_processing_run_is_retried()
    {
        await using var sp = Services(TempDb());
        var (tenant, wf) = await SeedAsync(sp);

        Guid runId;
        using (var scope = sp.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant, "owner@test");
            var uploads = SampleData.DriverSettlement().Select(f => new UploadedFile(f.SourceName, f.FileName, f.Content)).ToList();
            runId = (await scope.ServiceProvider.GetRequiredService<RunService>().EnqueueAsync(wf, uploads, null)).Run!.Id;
            // Simulate a worker that claimed the run and then crashed 10 minutes ago.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = DateTime.UtcNow.AddMinutes(-10);
            await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatus.Processing).SetProperty(r => r.HeartbeatAt, stale).SetProperty(r => r.Attempts, 1));
        }

        var worker = sp.GetRequiredService<IHostedService>();
        await worker.StartAsync(CancellationToken.None);
        var run = await WaitForAsync(sp, tenant, runId, RunStatus.Completed, RunStatus.Failed);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.Attempts);
    }
}
