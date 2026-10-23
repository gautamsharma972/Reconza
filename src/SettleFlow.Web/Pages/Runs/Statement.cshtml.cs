using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Model;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Runs;

/// <summary>Itemized statement for one group (e.g. one driver) — what they earned, what was deducted, and why.</summary>
public sealed class StatementModel(AppDbContext db, RunService runs, ITenantContext tenant) : PageModel
{
    public Run Run { get; private set; } = null!;
    public WorkflowDefinition Def { get; private set; } = null!;
    public SummaryLine Line { get; private set; } = null!;
    public string OrgName { get; private set; } = "";
    public string? DisplayName { get; private set; }
    public (string? From, string? To) Period { get; private set; }
    public List<(SourceDef Source, List<FieldDef> Columns, List<DetailRow> Rows)> Sections { get; } = new();
    public List<RunException> Exceptions { get; private set; } = new();
    public string? PrevGroup { get; private set; }
    public string? NextGroup { get; private set; }
    public bool DetailsAvailable { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, string group)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id && r.Status == RunStatus.Completed);
        if (run is null) return NotFound();
        Run = run;
        Def = WorkflowJson.Deserialize(run.DefinitionSnapshotJson);
        var summary = JsonSerializer.Deserialize<RunSummary>(run.SummaryJson, WorkflowJson.Options) ?? new();
        var idx = summary.Lines.FindIndex(l => l.Group == group);
        if (idx < 0) return NotFound();
        Line = summary.Lines[idx];
        PrevGroup = idx > 0 ? summary.Lines[idx - 1].Group : null;
        NextGroup = idx < summary.Lines.Count - 1 ? summary.Lines[idx + 1].Group : null;
        OrgName = (await db.Tenants.FindAsync(tenant.TenantId))?.Name ?? "";

        var details = await runs.ReadDetailsAsync(run);
        DetailsAvailable = run.DetailsStorageKey is not null;
        var mine = details.Where(d => d.Group == group).ToList();

        var nameField = Def.Fields.FirstOrDefault(f => f.Type == FieldType.Text && f.Name != Def.GroupByField && f.Name.EndsWith("Name", StringComparison.OrdinalIgnoreCase));
        DisplayName = nameField is null ? null : mine.Select(d => d.Values.GetValueOrDefault(nameField.Name)).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var dateFields = Def.Fields.Where(f => f.Type == FieldType.Date).Select(f => f.Name).ToList();
        var dates = mine.Where(d => d.Included).SelectMany(d => dateFields.Select(f => d.Values.GetValueOrDefault(f))).OfType<string>().Order().ToList();
        Period = (dates.FirstOrDefault(), dates.LastOrDefault());

        foreach (var s in Def.Sources)
        {
            var rows = mine.Where(d => d.Source == s.Name).OrderBy(d => d.Values.GetValueOrDefault(dateFields.FirstOrDefault() ?? "")).ThenBy(d => d.Row).ToList();
            if (rows.Count == 0) continue;
            var cols = s.ColumnMap.Keys.Select(Def.Field).OfType<FieldDef>()
                .Where(f => f.Name != Def.GroupByField && f.Name != s.AmountField && f != nameField).ToList();
            Sections.Add((s, cols, rows));
        }

        var rowKeys = mine.Select(d => (d.Source, d.Row)).ToHashSet();
        var exceptions = await db.RunExceptions.Where(e => e.RunId == id && e.RowNumber != null).ToListAsync();
        Exceptions = exceptions.Where(e => rowKeys.Contains((e.Source, e.RowNumber!.Value))).ToList();
        return Page();
    }
}
