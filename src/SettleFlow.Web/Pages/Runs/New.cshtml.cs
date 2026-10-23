using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Model;
using SettleFlow.Core.Presets;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Runs;

[RequestSizeLimit(210L * 1024 * 1024)]
public sealed class NewModel(AppDbContext db, RunService runs, ITenantContext tenant) : PageModel
{
    public Workflow Workflow { get; private set; } = null!;
    public WorkflowDefinition Def { get; private set; } = null!;
    public List<string> Errors { get; private set; } = new();
    public List<string> SetupIssues { get; private set; } = new();
    public bool SamplesAvailable { get; private set; }
    public PlanLimits Limits { get; private set; } = Plans.For(Plan.Trial);
    /// <summary>File name used for each source last time — helps auto-assign dropped files.</summary>
    public Dictionary<string, string> LastFileNames { get; private set; } = new();

    [BindProperty] public string? Label { get; set; }

    private async Task<bool> LoadAsync(Guid workflowId)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == workflowId);
        if (wf is null) return false;
        Workflow = wf;
        Def = WorkflowJson.Deserialize(wf.DefinitionJson);
        SetupIssues = Def.Validate();
        var sampleSources = SampleData.DriverSettlement().Select(s => s.SourceName).ToHashSet();
        SamplesAvailable = wf.PresetKey == "driver-settlement" && Def.Sources.All(s => sampleSources.Contains(s.Name));
        Limits = Plans.For((await db.Tenants.FindAsync(tenant.TenantId))!.Plan);
        var lastRun = await db.Runs.Include(r => r.Files).Where(r => r.WorkflowId == wf.Id).OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
        LastFileNames = lastRun?.Files.ToDictionary(f => f.SourceName, f => f.FileName) ?? new();
        return true;
    }

    public async Task<IActionResult> OnGetAsync(Guid workflowId)
    {
        if (!await LoadAsync(workflowId)) return NotFound();
        Label = $"Week {System.Globalization.ISOWeek.GetWeekOfYear(DateTime.UtcNow)} — {DateTime.UtcNow:dd MMM yyyy}";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid workflowId, CancellationToken ct)
    {
        if (!await LoadAsync(workflowId)) return NotFound();
        var uploads = new List<UploadedFile>();
        foreach (var (source, i) in Def.Sources.Select((s, i) => (s, i)))
        {
            var file = Request.Form.Files.GetFile($"file_{i}");
            if (file is null || file.Length == 0) continue;
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            uploads.Add(new UploadedFile(source.Name, Path.GetFileName(file.FileName), ms.ToArray()));
        }
        return await EnqueueAsync(uploads, ct);
    }

    public async Task<IActionResult> OnPostSampleAsync(Guid workflowId, CancellationToken ct)
    {
        if (!await LoadAsync(workflowId)) return NotFound();
        if (!SamplesAvailable) return BadRequest();
        var uploads = SampleData.DriverSettlement().Select(s => new UploadedFile(s.SourceName, s.FileName, s.Content)).ToList();
        Label = "Demo run with sample files";
        return await EnqueueAsync(uploads, ct);
    }

    private async Task<IActionResult> EnqueueAsync(List<UploadedFile> uploads, CancellationToken ct)
    {
        var outcome = await runs.EnqueueAsync(Workflow, uploads, Label, ct);
        if (outcome.Run is null) { Errors = outcome.Errors; return Page(); }
        return RedirectToPage("Details", new { id = outcome.Run.Id });
    }
}
