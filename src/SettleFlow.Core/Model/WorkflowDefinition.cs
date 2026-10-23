namespace SettleFlow.Core.Model;

public enum FieldType { Text, Number, Date }

/// <summary>How a source's amounts affect the settlement for a group (e.g. a driver).</summary>
public enum SourceRole
{
    /// <summary>The system of record (e.g. trips). Other sources are matched against it.</summary>
    Primary,
    /// <summary>Amounts added to the settlement (e.g. bonuses, reimbursements).</summary>
    Addition,
    /// <summary>Amounts subtracted from the settlement (e.g. fuel advances, deductions).</summary>
    Deduction,
    /// <summary>Used only for reconciliation checks (e.g. a customer's shipment report); no effect on totals.</summary>
    ReferenceOnly
}

/// <summary>A canonical field that sources map their columns onto.</summary>
public sealed class FieldDef
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public FieldType Type { get; set; } = FieldType.Text;
    /// <summary>Header synonyms used to suggest column mappings.</summary>
    public List<string> Synonyms { get; set; } = new();
}

public sealed class SourceDef
{
    public string Name { get; set; } = "";
    public SourceRole Role { get; set; } = SourceRole.Primary;

    /// <summary>Canonical field name → column header in the uploaded file.</summary>
    public Dictionary<string, string> ColumnMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fields that must be present and non-empty on every row.</summary>
    public List<string> RequiredFields { get; set; } = new();

    /// <summary>Composite key identifying a unique record within this source (duplicate detection).</summary>
    public List<string> KeyFields { get; set; } = new();

    /// <summary>Fields used to match this source's rows to primary rows. Empty = not matched.</summary>
    public List<string> MatchFields { get; set; } = new();

    /// <summary>Flag primary rows that have no matching row in this source.</summary>
    public bool ExpectEveryPrimaryRow { get; set; }

    /// <summary>Exclude rows that don't match any primary row from settlement totals.</summary>
    public bool ExcludeUnmatchedFromTotals { get; set; }

    /// <summary>The amount field summed into the settlement.</summary>
    public string? AmountField { get; set; }

    /// <summary>Optional: compare this field against <see cref="PrimaryCompareField"/> on the matched primary row.</summary>
    public string? CompareField { get; set; }
    public string? PrimaryCompareField { get; set; }
    public decimal CompareTolerance { get; set; } = 0.01m;

    /// <summary>Preferred date format(s) for this source, e.g. "dd/MM/yyyy". Tried before the defaults.</summary>
    public List<string> DateFormats { get; set; } = new();

    /// <summary>Optional worksheet name; defaults to the first sheet.</summary>
    public string? SheetName { get; set; }
}

public sealed class ReportTemplate
{
    public string Title { get; set; } = "Settlement Report";
    public string GroupLabel { get; set; } = "Group";
    public string NetLabel { get; set; } = "Net Payable";
    public string CurrencyFormat { get; set; } = "#,##0.00";
    public bool IncludeExceptionsSheet { get; set; } = true;
    public bool IncludeDetailSheets { get; set; } = true;
    /// <summary>Source name → column label in the settlement sheet. Missing = source name.</summary>
    public Dictionary<string, string> SourceLabels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorkflowDefinition
{
    public List<FieldDef> Fields { get; set; } = new();
    public List<SourceDef> Sources { get; set; } = new();
    /// <summary>Field that settlement totals are grouped by (e.g. DriverId).</summary>
    public string GroupByField { get; set; } = "";
    public ReportTemplate Report { get; set; } = new();

    public FieldDef? Field(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public SourceDef? PrimarySource => Sources.FirstOrDefault(s => s.Role == SourceRole.Primary);

    /// <summary>Configuration problems that would make a run meaningless.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (Sources.Count == 0) errors.Add("Add at least one source.");
        var primaries = Sources.Count(s => s.Role == SourceRole.Primary);
        if (primaries != 1) errors.Add("Exactly one source must have the Primary role.");
        if (string.IsNullOrWhiteSpace(GroupByField) || Field(GroupByField) is null)
            errors.Add("Choose a valid group-by field.");
        foreach (var dup in Sources.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Source name '{dup.Key}' is used more than once.");

        foreach (var s in Sources)
        {
            IEnumerable<string> referenced = s.RequiredFields.Concat(s.KeyFields).Concat(s.MatchFields);
            if (s.AmountField is not null) referenced = referenced.Append(s.AmountField);
            if (s.CompareField is not null) referenced = referenced.Append(s.CompareField);
            foreach (var f in referenced.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Field(f) is null) errors.Add($"{s.Name}: unknown field '{f}'.");
                else if (!s.ColumnMap.ContainsKey(f)) errors.Add($"{s.Name}: field '{f}' is used but not mapped to a column.");
            }
            if (s.AmountField is not null && Field(s.AmountField)?.Type != FieldType.Number)
                errors.Add($"{s.Name}: amount field '{s.AmountField}' must be a Number field.");
            if (s.Role is SourceRole.Addition or SourceRole.Deduction && s.AmountField is null)
                errors.Add($"{s.Name}: Addition/Deduction sources need an amount field.");
            if (s.Role != SourceRole.Primary && s.MatchFields.Count == 0 && !s.ColumnMap.ContainsKey(GroupByField))
                errors.Add($"{s.Name}: map the group-by field or set match fields so rows can be attributed.");
            if ((s.CompareField is null) != (s.PrimaryCompareField is null))
                errors.Add($"{s.Name}: set both compare fields or neither.");
            if (s.CompareField is not null && s.MatchFields.Count == 0)
                errors.Add($"{s.Name}: amount comparison requires match fields.");
        }
        if (PrimarySource is { } p)
        {
            if (!p.ColumnMap.ContainsKey(GroupByField)) errors.Add($"{p.Name}: the primary source must map the group-by field.");
            foreach (var s in Sources.Where(s => s.Role != SourceRole.Primary))
            {
                foreach (var f in s.MatchFields.Where(f => !p.ColumnMap.ContainsKey(f)))
                    errors.Add($"{s.Name}: match field '{f}' is not mapped on the primary source.");
                if (s.PrimaryCompareField is not null && !p.ColumnMap.ContainsKey(s.PrimaryCompareField))
                    errors.Add($"{s.Name}: compare field '{s.PrimaryCompareField}' is not mapped on the primary source.");
            }
        }
        return errors;
    }
}
