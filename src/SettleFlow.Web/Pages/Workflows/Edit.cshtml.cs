using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Model;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Workflows;

public sealed partial class EditModel(AppDbContext db, AuditLog audit) : PageModel
{
    public sealed class FieldRow
    {
        public string Name { get; set; } = "";
        public string Label { get; set; } = "";
        public FieldType Type { get; set; }
        public string Synonyms { get; set; } = "";
        public bool Remove { get; set; }
        public bool IsNew { get; set; }
    }

    public Workflow Workflow { get; private set; } = null!;
    public WorkflowDefinition Def { get; private set; } = null!;
    public List<string> Issues { get; private set; } = new();

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string GroupByField { get; set; } = "";
    [BindProperty] public ReportTemplate Report { get; set; } = new();
    [BindProperty] public List<FieldRow> Fields { get; set; } = new();
    [BindProperty] public string? NewSourceName { get; set; }
    [BindProperty] public SourceRole NewSourceRole { get; set; } = SourceRole.Deduction;

    private async Task<bool> LoadAsync(Guid id)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id);
        if (wf is null) return false;
        Workflow = wf;
        Def = WorkflowJson.Deserialize(wf.DefinitionJson);
        return true;
    }

    private void Populate()
    {
        Name = Workflow.Name;
        GroupByField = Def.GroupByField;
        Report = Def.Report;
        Fields = Def.Fields.Select(f => new FieldRow { Name = f.Name, Label = f.Label, Type = f.Type, Synonyms = string.Join(", ", f.Synonyms) }).ToList();
        Fields.Add(new FieldRow { IsNew = true });
        Issues = Def.Validate();
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        Populate();
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Name is required.");

        var fields = new List<FieldDef>();
        foreach (var row in Fields)
        {
            if (row.IsNew && string.IsNullOrWhiteSpace(row.Name)) continue;
            var existing = Def.Field(row.Name);
            if (row.Remove)
            {
                if (existing is not null) RemoveFieldReferences(existing.Name);
                continue;
            }
            if (row.IsNew)
            {
                if (!Identifier().IsMatch(row.Name)) { errors.Add($"Field name '{row.Name}' must start with a letter and use only letters and digits."); continue; }
                if (Def.Field(row.Name) is not null || fields.Any(f => f.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase))) { errors.Add($"Field '{row.Name}' already exists."); continue; }
            }
            else if (existing is null) continue;
            else if (existing.Type != row.Type && Def.Sources.Any(s => s.AmountField == existing.Name) && row.Type != FieldType.Number)
            { errors.Add($"'{existing.Label}' is used as an amount and must stay a Number."); row.Type = existing.Type; }

            fields.Add(new FieldDef
            {
                Name = existing?.Name ?? row.Name.Trim(),
                Label = string.IsNullOrWhiteSpace(row.Label) ? row.Name.Trim() : row.Label.Trim(),
                Type = row.Type,
                Synonyms = row.Synonyms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            });
        }
        if (errors.Count > 0)
        {
            foreach (var e in errors) ModelState.AddModelError("", e);
            Def = WorkflowJson.Deserialize(Workflow.DefinitionJson);
            Issues = Def.Validate();
            return Page();
        }

        Def.Fields = fields;
        Def.GroupByField = GroupByField;
        Report.SourceLabels = new(Report.SourceLabels.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)), StringComparer.OrdinalIgnoreCase);
        Def.Report = Report;
        Workflow.Name = Name.Trim();
        await SaveAsync("workflow.updated", "Settings, fields or report template changed.");
        TempData["Flash"] = "Saved.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddSourceAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        var name = NewSourceName?.Trim() ?? "";
        if (name.Length is 0 or > 40 || Def.Sources.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Flash"] = "Source names must be unique and 1–40 characters.";
            return RedirectToPage(new { id });
        }
        Def.Sources.Add(new SourceDef { Name = name, Role = Def.Sources.Count == 0 ? SourceRole.Primary : NewSourceRole });
        await SaveAsync("workflow.source_added", $"Source '{name}' added.");
        return RedirectToPage("Source", new { id, source = name });
    }

    public async Task<IActionResult> OnPostRemoveSourceAsync(Guid id, string source)
    {
        if (!await LoadAsync(id)) return NotFound();
        Def.Sources.RemoveAll(s => s.Name == source);
        await SaveAsync("workflow.source_removed", $"Source '{source}' removed.");
        TempData["Flash"] = $"Removed source '{source}'.";
        return RedirectToPage(new { id });
    }

    private void RemoveFieldReferences(string field)
    {
        foreach (var s in Def.Sources)
        {
            s.ColumnMap.Remove(field);
            s.RequiredFields.Remove(field); s.KeyFields.Remove(field); s.MatchFields.Remove(field);
            if (s.AmountField == field) s.AmountField = null;
            if (s.CompareField == field) s.CompareField = null;
            if (s.PrimaryCompareField == field) s.PrimaryCompareField = null;
        }
        if (Def.GroupByField == field) GroupByField = "";
    }

    private async Task SaveAsync(string action, string details)
    {
        Workflow.DefinitionJson = WorkflowJson.Serialize(Def);
        Workflow.UpdatedAt = DateTime.UtcNow;
        audit.Record(action, $"'{Workflow.Name}': {details}", Workflow.Id);
        await db.SaveChangesAsync();
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$")] private static partial Regex Identifier();
}
