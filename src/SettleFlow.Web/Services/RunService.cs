using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Engine;
using SettleFlow.Core.Model;
using SettleFlow.Core.Parsing;
using SettleFlow.Core.Reporting;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public sealed record UploadedFile(string SourceName, string FileName, byte[] Content);

public sealed record RunOutcome(Run? Run, List<string> Errors);

public sealed class SummaryLine
{
    public string Group { get; set; } = "";
    public Dictionary<string, decimal> SourceTotals { get; set; } = new();
    public Dictionary<string, int> SourceCounts { get; set; } = new();
    public decimal Net { get; set; }
    public int ExceptionCount { get; set; }
}

public sealed class RunSummary
{
    public List<SummaryLine> Lines { get; set; } = new();
    public Dictionary<string, SourceStats> Stats { get; set; } = new();
}

/// <summary>One normalized source row, kept so each group (e.g. driver) can get an itemized statement.</summary>
public sealed class DetailRow
{
    public string Source { get; set; } = "";
    public int Row { get; set; }
    public string? Group { get; set; }
    public bool Included { get; set; }
    public decimal? Amount { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();
}

public sealed class RunService(
    AppDbContext db, ITenantContext tenant, IFileStore files, AuditLog audit, UploadInspector inspector,
    RunQueueSignal signal, IEmailSender email, AppUrls urls, ILogger<RunService> log)
{
    /// <summary>
    /// Fast, in-request part of a run: check plan limits, inspect and scan every file, store them encrypted
    /// and queue the run. Parsing and reconciliation happen in <see cref="RunWorker"/>.
    /// </summary>
    public async Task<RunOutcome> EnqueueAsync(Workflow workflow, IReadOnlyList<UploadedFile> uploads, string? label, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant.");
        var org = await db.Tenants.SingleAsync(t => t.Id == tenantId, ct);
        var limits = Plans.For(org.Plan);
        var def = WorkflowJson.Deserialize(workflow.DefinitionJson);

        errors.AddRange(def.Validate().Select(e => "Workflow setup: " + e));
        if (errors.Count > 0) return new(null, errors);

        var month = await MonthUsageAsync(null, ct);
        if (limits.MonthlyRuns is { } maxRuns && month.Runs >= maxRuns)
            return new(null, [$"Your {limits.Name} plan includes {maxRuns} runs per month. Upgrade to keep going."]);
        if (month.Rows >= limits.MonthlyRows)
            return new(null, [$"You've used this month's {limits.MonthlyRows:N0} row allowance."]);

        var run = new Run
        {
            TenantId = tenantId,
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            Status = RunStatus.Queued,
            Stage = "Waiting to start",
            CreatedBy = tenant.UserName,
            DefinitionSnapshotJson = workflow.DefinitionJson,
        };

        foreach (var source in def.Sources)
        {
            var up = uploads.FirstOrDefault(u => string.Equals(u.SourceName, source.Name, StringComparison.OrdinalIgnoreCase));
            if (up is null) { errors.Add($"{source.Name}: no file uploaded."); continue; }
            var inspection = await inspector.InspectAsync(up.FileName, up.Content, limits.MaxFileMb * 1024L * 1024L, ct);
            if (!inspection.Ok) { errors.Add($"{source.Name} ({up.FileName}): {inspection.Error}"); continue; }
            run.Files.Add(new RunFile
            {
                TenantId = tenantId, RunId = run.Id, SourceName = source.Name, FileName = Path.GetFileName(up.FileName),
                SizeBytes = up.Content.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(up.Content)),
                ScanStatus = inspection.Scan.Verdict.ToString(), ScanDetail = inspection.Scan.Detail,
            });
        }
        if (errors.Count > 0)
        {
            await db.SaveChangesAsync(ct); // persist any upload.blocked audit events
            return new(null, errors);
        }

        // Store only after every file passed, so a rejected batch leaves nothing behind.
        foreach (var f in run.Files)
            f.StorageKey = await files.SaveAsync(tenantId, "uploads", uploads.First(u => u.SourceName.Equals(f.SourceName, StringComparison.OrdinalIgnoreCase)).Content, ct);

        run.Number = await db.Runs.CountAsync(r => r.WorkflowId == workflow.Id, ct) + 1;
        run.Label = string.IsNullOrWhiteSpace(label) ? $"Run #{run.Number} — {DateTime.UtcNow:dd MMM yyyy}" : label.Trim();
        db.Runs.Add(run);
        audit.Record("run.queued", $"{workflow.Name}: {run.Label}. Files: " +
            string.Join(", ", run.Files.Select(f => $"{f.FileName} (sha256 {f.Sha256[..12]}…, scan {f.ScanStatus})")), run.Id);
        await db.SaveChangesAsync(ct);
        signal.Notify();
        return new(run, errors);
    }

    /// <summary>Worker-side processing of a claimed run. The caller has already set the tenant context.</summary>
    public async Task ProcessAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Runs.Include(r => r.Files).SingleAsync(r => r.Id == runId, ct);
        var sw = Stopwatch.StartNew();
        try
        {
            var org = await db.Tenants.SingleAsync(t => t.Id == run.TenantId, ct);
            var limits = Plans.For(org.Plan);
            var def = WorkflowJson.Deserialize(run.DefinitionSnapshotJson);
            var rowsLeft = limits.MonthlyRows - (await MonthUsageAsync(run.Id, ct)).Rows;

            var tables = new Dictionary<string, RawTable>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var step = 0;
            foreach (var source in def.Sources)
            {
                await ReportAsync(run, $"Reading {source.Name}", 10 + 50 * step++ / def.Sources.Count, ct);
                var file = run.Files.Single(f => f.SourceName == source.Name);
                var bytes = await files.ReadAsync(run.TenantId, file.StorageKey ?? throw new RunInputException("The uploaded files were deleted before processing. Please start the run again."), ct);
                try
                {
                    using var ms = new MemoryStream(bytes, writable: false);
                    var table = TabularFileReader.Read(ms, TabularFileReader.FormatFromFileName(file.FileName), source.SheetName, Math.Max(rowsLeft, 0));
                    tables[source.Name] = table;
                    file.Rows = table.Rows.Count;
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ExcelDataReader.Exceptions.ExcelReaderException)
                {
                    errors.Add($"{source.Name} ({file.FileName}): {ex.Message}");
                }
            }
            var totalRows = tables.Values.Sum(t => t.Rows.Count);
            if (errors.Count == 0 && totalRows > rowsLeft)
                errors.Add($"These files contain {totalRows:N0} rows but only {Math.Max(rowsLeft, 0):N0} remain in this month's allowance.");
            if (errors.Count > 0) throw new RunInputException(string.Join(" ", errors));
            run.RowsProcessed = totalRows;

            await ReportAsync(run, "Reconciling", 65, ct);
            var result = ReconciliationEngine.Run(def, tables);

            await ReportAsync(run, "Writing report", 85, ct);
            using (var report = new MemoryStream())
            {
                ExcelReportWriter.Write(result,
                    new ReportContext(run.WorkflowName, run.Label, DateTimeOffset.UtcNow, run.CreatedBy, run.Files.ToDictionary(f => f.SourceName, f => f.FileName)),
                    report);
                run.ReportStorageKey = await files.SaveAsync(run.TenantId, "reports", report.ToArray(), ct);
            }
            run.DetailsStorageKey = await files.SaveAsync(run.TenantId, "details", Compress(BuildDetails(result)), ct);

            run.GroupCount = result.Lines.Count;
            run.ExceptionCount = result.Exceptions.Count;
            run.NetTotal = result.NetTotal;
            run.SummaryJson = WorkflowJson.Serialize(new RunSummary
            {
                Lines = result.Lines.Select(l => new SummaryLine
                {
                    Group = l.Group, Net = l.Net, ExceptionCount = l.ExceptionCount,
                    SourceTotals = new(l.SourceTotals), SourceCounts = new(l.SourceCounts),
                }).ToList(),
                Stats = new(result.Stats),
            });
            db.RunExceptions.AddRange(result.Exceptions.Select(e => new RunException
            {
                TenantId = run.TenantId, RunId = run.Id, Source = e.Source, RowNumber = e.RowNumber, Type = e.Type.ToString(),
                Key = e.Key, Message = e.Message, Amount = e.Amount, ExcludedFromTotals = e.ExcludedFromTotals,
            }));
            run.Status = RunStatus.Completed;
            run.Stage = "Done";
            run.Progress = 100;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not (RunInputException or ReconciliationInputException)) log.LogError(ex, "Run {RunId} failed", run.Id);
            run.Status = RunStatus.Failed;
            run.Stage = "Failed";
            run.Error = ex is RunInputException or ReconciliationInputException
                ? ex.Message
                : "Unexpected processing error. It has been logged — please try again, and contact us if it keeps happening.";
        }
        run.DurationMs = sw.ElapsedMilliseconds;
        run.CompletedAt = DateTime.UtcNow;
        audit.Record(run.Status == RunStatus.Completed ? "run.completed" : "run.failed",
            $"{run.WorkflowName}: {run.Label}. " + (run.Status == RunStatus.Completed
                ? $"{run.RowsProcessed:N0} rows, {run.ExceptionCount} exceptions, net {run.NetTotal.ToString("N2", CultureInfo.InvariantCulture)}."
                : run.Error), run.Id);
        await db.SaveChangesAsync(CancellationToken.None);
        await NotifyAsync(run);
    }

    /// <summary>Emails the person who started the run when they were kept waiting, or when it failed.</summary>
    private async Task NotifyAsync(Run run)
    {
        var waited = (run.CompletedAt ?? DateTime.UtcNow) - run.CreatedAt;
        if (!run.CreatedBy.Contains('@') || (run.Status == RunStatus.Completed && waited < TimeSpan.FromSeconds(30))) return;
        try
        {
            await email.SendAsync(EmailTemplates.RunFinished(run.CreatedBy, run.Label, run.WorkflowName,
                run.Status == RunStatus.Completed, run.ExceptionCount, urls.Run(run.Id)));
        }
        catch (Exception ex) { log.LogWarning(ex, "Run notification email failed for {RunId}", run.Id); }
    }

    public async Task<List<DetailRow>> ReadDetailsAsync(Run run, CancellationToken ct = default)
    {
        if (run.DetailsStorageKey is null) return new();
        var bytes = await files.ReadAsync(run.TenantId, run.DetailsStorageKey, ct);
        using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<List<DetailRow>>(gz, WorkflowJson.Options, ct) ?? new();
    }

    /// <summary>Deletes stored uploads, the report and row details for a run, keeping the summary and audit trail.</summary>
    public async Task PurgeFilesAsync(Run run, string reason, CancellationToken ct = default)
    {
        foreach (var f in run.Files.Where(f => f.StorageKey is not null))
        {
            await files.DeleteAsync(run.TenantId, f.StorageKey!, ct);
            f.StorageKey = null;
        }
        foreach (var key in new[] { run.ReportStorageKey, run.DetailsStorageKey }.OfType<string>())
            await files.DeleteAsync(run.TenantId, key, ct);
        run.ReportStorageKey = null;
        run.DetailsStorageKey = null;
        run.FilesPurgedAt = DateTime.UtcNow;
        audit.Record("run.files_deleted", $"{run.WorkflowName}: {run.Label}. {reason}", run.Id);
    }

    private async Task<(int Runs, int Rows)> MonthUsageAsync(Guid? excludeRun, CancellationToken ct)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var runs = await db.Runs.Where(r => r.CreatedAt >= monthStart && r.Status != RunStatus.Failed && r.Id != excludeRun)
            .Select(r => r.RowsProcessed).ToListAsync(ct);
        return (runs.Count, runs.Sum());
    }

    private async Task ReportAsync(Run run, string stage, int progress, CancellationToken ct)
    {
        run.Stage = stage;
        run.Progress = progress;
        var now = DateTime.UtcNow;
        await db.Runs.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Stage, stage).SetProperty(r => r.Progress, progress).SetProperty(r => r.HeartbeatAt, now), ct);
    }

    private static List<DetailRow> BuildDetails(RunResult result)
    {
        var def = result.Definition;
        return result.Rows.SelectMany(kv => kv.Value.Select(r => new DetailRow
        {
            Source = r.Source, Row = r.RowNumber, Group = r.Group, Included = !r.Excluded && r.Group is not null, Amount = r.Amount,
            Values = r.Values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value switch
            {
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                decimal m => m.ToString(CultureInfo.InvariantCulture),
                var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""
            }),
        })).ToList();
    }

    private static byte[] Compress(List<DetailRow> rows)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            JsonSerializer.Serialize(gz, rows, WorkflowJson.Options);
        return ms.ToArray();
    }

    private sealed class RunInputException(string message) : Exception(message);
}
