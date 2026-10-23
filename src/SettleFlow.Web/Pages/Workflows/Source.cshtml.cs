using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Core.Mapping;
using SettleFlow.Core.Model;
using SettleFlow.Core.Parsing;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Workflows;

public sealed class SourceModel(AppDbContext db, AuditLog audit, IMappingSuggester suggester, ITenantContext tenant, UploadInspector inspector) : PageModel
{
    public Workflow Workflow { get; private set; } = null!;
    public WorkflowDefinition Def { get; private set; } = null!;
    public SourceDef Source { get; private set; } = null!;
    public List<string> Headers { get; private set; } = new();
    public List<List<string>> Preview { get; private set; } = new();
    public Dictionary<string, MappingSuggestion> Suggestions { get; private set; } = new();
    public string? AnalyzeMessage { get; private set; }
    public bool AnalyzeFailed { get; private set; }
    public string? AiNotice { get; private set; }
    public List<string> SheetNames { get; private set; } = new();

    [BindProperty(SupportsGet = true)] public string SourceName { get; set; } = "";
    [BindProperty] public SourceRole Role { get; set; }
    [BindProperty] public Dictionary<string, string?> Map { get; set; } = new();
    [BindProperty] public List<string> Required { get; set; } = new();
    [BindProperty] public List<string> Key { get; set; } = new();
    [BindProperty] public List<string> Match { get; set; } = new();
    [BindProperty] public string? AmountField { get; set; }
    [BindProperty] public string? CompareField { get; set; }
    [BindProperty] public string? PrimaryCompareField { get; set; }
    [BindProperty] public decimal CompareTolerance { get; set; }
    [BindProperty] public bool ExpectEveryPrimaryRow { get; set; }
    [BindProperty] public bool ExcludeUnmatchedFromTotals { get; set; }
    [BindProperty] public string? DateFormats { get; set; }
    [BindProperty] public string? SheetName { get; set; }
    [BindProperty] public string? HeadersJson { get; set; }
    [BindProperty] public IFormFile? SampleFile { get; set; }

    public SourceDef? Primary => Def.PrimarySource;

    private async Task<bool> LoadAsync(Guid id)
    {
        var wf = await db.Workflows.FirstOrDefaultAsync(w => w.Id == id);
        if (wf is null) return false;
        Workflow = wf;
        Def = WorkflowJson.Deserialize(wf.DefinitionJson);
        var s = Def.Sources.FirstOrDefault(s => s.Name == SourceName);
        if (s is null) return false;
        Source = s;
        return true;
    }

    private void PopulateFromSource()
    {
        Role = Source.Role;
        Map = Def.Fields.ToDictionary(f => f.Name, f => Source.ColumnMap.GetValueOrDefault(f.Name));
        Required = Source.RequiredFields; Key = Source.KeyFields; Match = Source.MatchFields;
        AmountField = Source.AmountField; CompareField = Source.CompareField; PrimaryCompareField = Source.PrimaryCompareField;
        CompareTolerance = Source.CompareTolerance; ExpectEveryPrimaryRow = Source.ExpectEveryPrimaryRow;
        ExcludeUnmatchedFromTotals = Source.ExcludeUnmatchedFromTotals;
        DateFormats = string.Join(", ", Source.DateFormats); SheetName = Source.SheetName;
        Headers = Source.ColumnMap.Values.Distinct().ToList();
    }

    public async Task<IActionResult> OnGetAsync(Guid id, string source)
    {
        SourceName = source;
        if (!await LoadAsync(id)) return NotFound();
        PopulateFromSource();
        return Page();
    }

    public async Task<IActionResult> OnPostAnalyzeAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        RestoreHeaders();
        AnalyzeFailed = true;
        if (SampleFile is null) { AnalyzeMessage = "Choose a sample file first."; return Page(); }

        var limits = Plans.For((await db.Tenants.FindAsync(tenant.TenantId))!.Plan);
        using var ms = new MemoryStream();
        await SampleFile.CopyToAsync(ms);
        var inspection = await inspector.InspectAsync(SampleFile.FileName, ms.ToArray(), limits.MaxFileMb * 1024L * 1024L);
        if (!inspection.Ok) { AnalyzeMessage = inspection.Error; await db.SaveChangesAsync(); return Page(); }
        ms.Position = 0;
        RawTable table;
        try { table = TabularFileReader.Read(ms, inspection.Format, string.IsNullOrWhiteSpace(SheetName) ? null : SheetName, 50_000); }
        catch (Exception ex) when (ex is InvalidDataException or ExcelDataReader.Exceptions.ExcelReaderException)
        { AnalyzeMessage = ex.Message; return Page(); }
        AnalyzeFailed = false;

        SheetNames = table.SheetNames;
        Headers = table.Headers;
        HeadersJson = JsonSerializer.Serialize(Headers);
        Preview = table.Rows.Take(8).Select(r => r.Cells.Select(Display).ToList()).ToList();

        var outcome = await suggester.SuggestAsync(Def.Fields, Headers, Preview.Select(p => (IReadOnlyList<string>)p).ToList());
        var suggestions = outcome.Suggestions;
        AiNotice = outcome.Notice;
        Suggestions = suggestions.ToDictionary(s => s.Field);
        var applied = 0;
        foreach (var s in suggestions)
        {
            var current = Map.GetValueOrDefault(s.Field);
            var currentValid = current is not null && Headers.Contains(current, StringComparer.OrdinalIgnoreCase);
            if (!currentValid && s.Column is not null && s.Confidence >= 0.5) { Map[s.Field] = s.Column; applied++; }
            else if (!currentValid && current is not null) Map[s.Field] = null;
        }
        var aiCount = suggestions.Count(s => s.Origin != "Rules" && s.Column is not null);
        AnalyzeMessage = $"Read {table.Rows.Count:N0} rows from '{table.SheetName}' (header on row {(table.Rows.FirstOrDefault()?.RowNumber ?? 2) - 1}). " +
                         (applied > 0 ? $"Pre-filled {applied} column(s){(aiCount > 0 ? $", {aiCount} with AI help" : "")} — check them, then save." : "Check the mapping, then save.");
        ModelState.Clear(); // re-render bound values we changed
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        if (!await LoadAsync(id)) return NotFound();
        RestoreHeaders();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, column) in Map)
            if (!string.IsNullOrWhiteSpace(column) && Def.Field(field) is not null) map[field] = column.Trim();

        bool Mapped(string f) => map.ContainsKey(f);
        Source.Role = Role;
        Source.ColumnMap = map;
        Source.RequiredFields = Required.Where(Mapped).Distinct().ToList();
        Source.KeyFields = Key.Where(Mapped).Distinct().ToList();
        Source.MatchFields = Role == SourceRole.Primary ? new() : Match.Where(Mapped).Distinct().ToList();
        Source.AmountField = string.IsNullOrEmpty(AmountField) ? null : AmountField;
        var compare = Role != SourceRole.Primary && !string.IsNullOrEmpty(CompareField) && !string.IsNullOrEmpty(PrimaryCompareField);
        Source.CompareField = compare ? CompareField : null;
        Source.PrimaryCompareField = compare ? PrimaryCompareField : null;
        Source.CompareTolerance = Math.Max(0, CompareTolerance);
        Source.ExpectEveryPrimaryRow = Role != SourceRole.Primary && ExpectEveryPrimaryRow;
        Source.ExcludeUnmatchedFromTotals = Role != SourceRole.Primary && ExcludeUnmatchedFromTotals;
        Source.DateFormats = (DateFormats ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        Source.SheetName = string.IsNullOrWhiteSpace(SheetName) ? null : SheetName.Trim();

        Workflow.DefinitionJson = WorkflowJson.Serialize(Def);
        Workflow.UpdatedAt = DateTime.UtcNow;
        audit.Record("workflow.mapping_saved", $"'{Workflow.Name}' / {Source.Name}: " +
            string.Join(", ", map.Select(kv => $"{kv.Value}→{kv.Key}")), Workflow.Id);
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Saved mapping and rules for {Source.Name}.";
        return RedirectToPage("Edit", new { id });
    }

    private void RestoreHeaders()
    {
        try { Headers = HeadersJson is { Length: > 0 } ? JsonSerializer.Deserialize<List<string>>(HeadersJson) ?? new() : new(); }
        catch (JsonException) { Headers = new(); }
        if (Headers.Count == 0) Headers = Source.ColumnMap.Values.Distinct().ToList();
    }

    private static string Display(object? v) => v switch
    {
        null or DBNull => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : d.ToString("yyyy-MM-dd HH:mm"),
        double x => x.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
    };
}
