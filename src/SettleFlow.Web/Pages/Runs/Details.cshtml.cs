using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Model;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Runs;

public sealed class DetailsModel(AppDbContext db, IFileStore files, RunService runs, AuditLog audit, ITenantContext tenant) : PageModel
{
    public Run Run { get; private set; } = null!;
    public WorkflowDefinition Def { get; private set; } = null!;
    public RunSummary Summary { get; private set; } = new();
    public List<RunException> Exceptions { get; private set; } = new();
    public int QueuePosition { get; private set; }

    private async Task<bool> LoadAsync(Guid id, bool full = true)
    {
        var run = await db.Runs.Include(r => r.Files).FirstOrDefaultAsync(r => r.Id == id);
        if (run is null) return false;
        Run = run;
        if (!full) return true;
        Def = WorkflowJson.Deserialize(run.DefinitionSnapshotJson);
        Summary = JsonSerializer.Deserialize<RunSummary>(run.SummaryJson, WorkflowJson.Options) ?? new();
        Exceptions = await db.RunExceptions.Where(e => e.RunId == id).OrderBy(e => e.Source).ThenBy(e => e.RowNumber).ToListAsync();
        if (run.Status == RunStatus.Queued)
            QueuePosition = await db.Runs.IgnoreQueryFilters().CountAsync(r => r.Status == RunStatus.Queued && r.CreatedAt < run.CreatedAt) + 1;
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid id) => await LoadAsync(id) ? Page() : NotFound();

    /// <summary>Polled by the progress view while a run is queued or processing.</summary>
    public async Task<IActionResult> OnGetStatusAsync(Guid id)
    {
        var r = await db.Runs.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { status = r.Status.ToString(), r.Stage, r.Progress, r.Error }).FirstOrDefaultAsync();
        return r is null ? NotFound() : new JsonResult(r);
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, full: false) || Run.ReportStorageKey is null) return NotFound();
        var bytes = await files.ReadAsync(Run.TenantId, Run.ReportStorageKey, ct);
        audit.Record("report.downloaded", $"{Run.WorkflowName}: {Run.Label}", Run.Id);
        await db.SaveChangesAsync(ct);
        var name = string.Concat($"{Run.WorkflowName} - {Run.Label}".Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' ? '_' : c));
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx");
    }

    public async Task<IActionResult> OnGetFileAsync(Guid id, Guid fileId, CancellationToken ct)
    {
        if (!await LoadAsync(id, full: false)) return NotFound();
        var f = Run.Files.FirstOrDefault(f => f.Id == fileId);
        if (f?.StorageKey is null) return NotFound();
        var bytes = await files.ReadAsync(Run.TenantId, f.StorageKey, ct);
        audit.Record("upload.downloaded", $"{Run.Label}: {f.FileName}", Run.Id);
        await db.SaveChangesAsync(ct);
        return File(bytes, "application/octet-stream", f.FileName);
    }

    public async Task<IActionResult> OnPostReviewAsync(Guid id, long exceptionId, ExceptionStatus status, string? note)
    {
        var ex = await db.RunExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.RunId == id);
        if (ex is null) return NotFound();
        ex.Status = status;
        note = note?.Trim();
        ex.Note = string.IsNullOrEmpty(note) ? null : note[..Math.Min(note.Length, 500)];
        ex.ReviewedBy = tenant.UserName;
        ex.ReviewedAt = DateTime.UtcNow;
        audit.Record("exception.reviewed", $"{ex.Source} row {ex.RowNumber} {ex.Type} → {status}{(ex.Note is null ? "" : $": {ex.Note}")}", id);
        await db.SaveChangesAsync();
        if (Request.Headers.XRequestedWith == "fetch") return new JsonResult(new { ok = true, status = status.ToString(), by = ex.ReviewedBy });
        return Redirect(Url.Page("Details", new { id })! + "#exceptions");
    }

    public async Task<IActionResult> OnPostAcceptTypeAsync(Guid id, string type)
    {
        var open = await db.RunExceptions.Where(e => e.RunId == id && e.Type == type && e.Status == ExceptionStatus.Open).ToListAsync();
        foreach (var e in open) { e.Status = ExceptionStatus.Accepted; e.ReviewedBy = tenant.UserName; e.ReviewedAt = DateTime.UtcNow; }
        audit.Record("exception.bulk_accepted", $"{open.Count} open {type} exceptions accepted.", id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Accepted {open.Count} {ExceptionHelp.For(type).Title.ToLowerInvariant()} exception(s).";
        return Redirect(Url.Page("Details", new { id })! + "#exceptions");
    }

    public async Task<IActionResult> OnPostDeleteFilesAsync(Guid id, CancellationToken ct)
    {
        if (!await LoadAsync(id, full: false)) return NotFound();
        if (Run.Status is RunStatus.Queued or RunStatus.Processing) return BadRequest();
        await runs.PurgeFilesAsync(Run, "Deleted manually.", ct);
        await db.SaveChangesAsync(ct);
        TempData["Flash"] = "Uploaded files and the report were permanently deleted. The check summary and activity record are kept.";
        return RedirectToPage(new { id });
    }
}
