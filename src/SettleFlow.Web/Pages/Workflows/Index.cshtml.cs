using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Presets;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Workflows;

public sealed class IndexModel(AppDbContext db, ITenantContext tenant, AuditLog audit) : PageModel
{
    public List<Workflow> Workflows { get; private set; } = new();
    public Dictionary<Guid, (int Runs, DateTime? Last)> RunStats { get; private set; } = new();
    public PlanLimits Limits { get; private set; } = Plans.For(Plan.Trial);

    [BindProperty, Required, StringLength(100)] public string Name { get; set; } = "";
    [BindProperty] public string Preset { get; set; } = "driver-settlement";

    private async Task LoadAsync()
    {
        Workflows = await db.Workflows.OrderBy(w => w.Name).ToListAsync();
        var runs = await db.Runs.GroupBy(r => r.WorkflowId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(r => (DateTime?)r.CreatedAt) }).ToListAsync();
        RunStats = runs.ToDictionary(r => r.Key, r => (r.Count, r.Last));
        Limits = Plans.For((await db.Tenants.FindAsync(tenant.TenantId))!.Plan);
    }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadAsync();
        var preset = WorkflowPresets.Find(Preset);
        if (preset is null) ModelState.AddModelError(nameof(Preset), "Choose a template.");
        if (Workflows.Count >= Limits.Workflows)
            ModelState.AddModelError("", $"Your {Limits.Name} plan allows {Limits.Workflows} saved workflows. Delete one or upgrade.");
        if (!ModelState.IsValid) return Page();

        var wf = new Workflow { Name = Name.Trim(), PresetKey = preset!.Key, DefinitionJson = WorkflowJson.Serialize(preset.Create()) };
        db.Workflows.Add(wf);
        audit.Record("workflow.created", $"'{wf.Name}' from template '{preset.Name}'.", wf.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = preset.Key == "blank"
            ? "Workflow created. Add fields and sources to define it."
            : "Workflow created from template. Review each source's column mapping against one of your real files, then run it.";
        return RedirectToPage("Edit", new { id = wf.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id);
        if (wf is null) return NotFound();
        db.Workflows.Remove(wf);
        audit.Record("workflow.deleted", $"'{wf.Name}' deleted. Past runs are kept.", wf.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Deleted '{wf.Name}'.";
        return RedirectToPage();
    }
}
