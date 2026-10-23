using System.Globalization;
using SettleFlow.Core.Model;
using SettleFlow.Core.Parsing;

namespace SettleFlow.Core.Engine;

/// <summary>
/// Deterministic reconciliation: normalize → validate → de-duplicate → match → compare → settle.
/// No step here involves an LLM; every number in the output is traceable to source rows.
/// </summary>
/// <summary>A problem with the user's files or workflow setup; its message is safe to show to the user.</summary>
public sealed class ReconciliationInputException(string message) : Exception(message);

public static class ReconciliationEngine
{
    public static RunResult Run(WorkflowDefinition def, IReadOnlyDictionary<string, RawTable> tables)
    {
        var configErrors = def.Validate();
        if (configErrors.Count > 0)
            throw new ReconciliationInputException("Workflow configuration is invalid: " + string.Join(" ", configErrors));

        var result = new RunResult { Definition = def };
        var primary = def.PrimarySource!;

        foreach (var source in def.Sources)
        {
            if (!tables.TryGetValue(source.Name, out var table))
                throw new ReconciliationInputException($"No file supplied for source '{source.Name}'.");
            result.Rows[source.Name] = Normalize(def, source, table, result.Exceptions);
            result.Stats[source.Name] = new SourceStats { RowsRead = table.Rows.Count };
        }

        foreach (var source in def.Sources)
            FlagDuplicates(def, source, result.Rows[source.Name], result.Exceptions);

        var primaryRows = result.Rows[primary.Name];
        foreach (var r in primaryRows.Where(r => r.Valid))
        {
            r.Group = KeyOf(def, r, [def.GroupByField]);
            r.Amount = primary.AmountField is null ? null : r.Values.GetValueOrDefault(primary.AmountField) as decimal?;
            if (r.Group.Length > 0 || r.Excluded) continue;
            r.Group = null;
            r.Excluded = true;
            result.Exceptions.Add(new(primary.Name, r.RowNumber, ExceptionType.Unattributed, "",
                $"{def.Field(def.GroupByField)?.Label ?? def.GroupByField} is empty; excluded from totals.", r.Amount, true));
        }

        foreach (var source in def.Sources.Where(s => s.Role != SourceRole.Primary))
            MatchAndAttribute(def, source, primary, primaryRows, result.Rows[source.Name], result.Exceptions);

        Settle(def, result);
        return result;
    }

    private static List<NormalizedRow> Normalize(WorkflowDefinition def, SourceDef source, RawTable table, List<ReconException> exceptions)
    {
        var rows = new List<NormalizedRow>();
        var missingColumns = source.ColumnMap
            .Where(kv => !table.Headers.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
            .ToList();
        foreach (var (field, column) in missingColumns)
            exceptions.Add(new(source.Name, null, ExceptionType.MissingColumn, field,
                $"Column '{column}' (mapped to {field}) was not found in the file. Columns found: {string.Join(", ", table.Headers)}.",
                null, true));
        var fatal = missingColumns.Any(m => source.RequiredFields.Contains(m.Key, StringComparer.OrdinalIgnoreCase)
                                            || string.Equals(m.Key, source.AmountField, StringComparison.OrdinalIgnoreCase));

        foreach (var raw in table.Rows)
        {
            var row = new NormalizedRow { Source = source.Name, RowNumber = raw.RowNumber };
            rows.Add(row);
            if (fatal) { row.Valid = false; row.Excluded = true; continue; }

            foreach (var (fieldName, column) in source.ColumnMap)
            {
                var field = def.Field(fieldName);
                if (field is null || missingColumns.Any(m => m.Key == fieldName)) continue;
                var cell = table.Get(raw, column);
                if (ValueNormalizer.TryNormalize(cell, field.Type, source.DateFormats, out var value))
                    row.Values[field.Name] = value;
                else
                {
                    row.Valid = false;
                    exceptions.Add(new(source.Name, raw.RowNumber, ExceptionType.InvalidValue, field.Name,
                        $"{field.Label}: '{ValueNormalizer.NormalizeText(cell)}' is not a valid {field.Type.ToString().ToLowerInvariant()}.",
                        null, true));
                }
            }

            foreach (var req in source.RequiredFields)
            {
                if (row.Values.TryGetValue(req, out var v) && v is not null && !(v is string s && s.Length == 0)) continue;
                if (!row.Values.ContainsKey(req) && !row.Valid) continue; // already reported as invalid
                row.Valid = false;
                exceptions.Add(new(source.Name, raw.RowNumber, ExceptionType.MissingValue, req,
                    $"{def.Field(req)?.Label ?? req} is empty.", null, true));
            }
            if (!row.Valid) row.Excluded = true;
        }
        return rows;
    }

    private static void FlagDuplicates(WorkflowDefinition def, SourceDef source, List<NormalizedRow> rows, List<ReconException> exceptions)
    {
        if (source.KeyFields.Count == 0) return;
        var firstSeen = new Dictionary<string, NormalizedRow>();
        foreach (var row in rows.Where(r => r.Valid))
        {
            var key = KeyOf(def, row, source.KeyFields);
            if (firstSeen.TryGetValue(key, out var first))
            {
                row.Excluded = true;
                var amount = source.AmountField is null ? null : row.Values.GetValueOrDefault(source.AmountField) as decimal?;
                exceptions.Add(new(source.Name, row.RowNumber, ExceptionType.Duplicate, Display(key),
                    $"Duplicate of row {first.RowNumber} on {string.Join(" + ", source.KeyFields)}; excluded from totals.",
                    amount, true));
            }
            else firstSeen[key] = row;
        }
    }

    private static void MatchAndAttribute(WorkflowDefinition def, SourceDef source, SourceDef primary,
        List<NormalizedRow> primaryRows, List<NormalizedRow> rows, List<ReconException> exceptions)
    {
        var active = rows.Where(r => !r.Excluded).ToList();
        foreach (var r in active)
            r.Amount = source.AmountField is null ? null : r.Values.GetValueOrDefault(source.AmountField) as decimal?;

        ILookup<string, NormalizedRow>? primaryIndex = null;
        if (source.MatchFields.Count > 0)
        {
            var livePrimary = primaryRows.Where(p => !p.Excluded).ToList();
            foreach (var p in livePrimary) p.MatchKey = KeyOf(def, p, source.MatchFields);
            primaryIndex = livePrimary.ToLookup(p => p.MatchKey!);
            foreach (var r in active) r.MatchKey = KeyOf(def, r, source.MatchFields);
        }

        var ownsGroup = source.ColumnMap.ContainsKey(def.GroupByField);
        foreach (var r in active)
        {
            var ownGroup = ownsGroup ? KeyOf(def, r, [def.GroupByField]) : null;
            if (ownGroup is { Length: 0 }) ownGroup = null;
            string? matchedGroup = null;

            if (primaryIndex is not null)
            {
                var candidates = primaryIndex[r.MatchKey!].ToList();
                if (candidates.Count == 0)
                {
                    // One exception per unmatched row, stating plainly whether it counts towards totals.
                    var reference = source.Role == SourceRole.ReferenceOnly;
                    var exclude = !reference && (source.ExcludeUnmatchedFromTotals || ownGroup is null);
                    var outcome = reference ? ""
                        : exclude && ownGroup is null && !source.ExcludeUnmatchedFromTotals
                            ? $" Excluded from totals: no {def.Field(def.GroupByField)?.Label ?? def.GroupByField} to attribute it to."
                            : exclude ? " Excluded from totals." : $" Included in totals for {Display(ownGroup!)}.";
                    exceptions.Add(new(source.Name, r.RowNumber, ExceptionType.Unmatched, Display(r.MatchKey!),
                        $"No {primary.Name} record matches {Describe(def, r, source.MatchFields)}.{outcome}",
                        r.Amount, exclude));
                    r.Group = ownGroup;
                    r.Excluded = exclude || reference && ownGroup is null;
                    continue;
                }
                else
                {
                    var groups = candidates.Select(c => c.Group).Distinct().ToList();
                    if (groups.Count == 1) matchedGroup = groups[0];
                }
            }

            if (ownGroup is not null && matchedGroup is not null && ownGroup != matchedGroup)
                exceptions.Add(new(source.Name, r.RowNumber, ExceptionType.GroupConflict, Display(r.MatchKey ?? ""),
                    $"{source.Name} attributes this row to {Display(ownGroup)} but the matching {primary.Name} record belongs to {Display(matchedGroup)}. Using {Display(ownGroup)}.",
                    r.Amount, false));

            r.Group = ownGroup ?? matchedGroup;
            if (r.Group is null)
            {
                r.Excluded = true;
                exceptions.Add(new(source.Name, r.RowNumber, ExceptionType.Unattributed, Display(r.MatchKey ?? ""),
                    $"Can't determine {def.Field(def.GroupByField)?.Label ?? def.GroupByField} for this row; excluded from totals.",
                    r.Amount, true));
            }
        }

        if (primaryIndex is null) return;

        if (source.ExpectEveryPrimaryRow)
        {
            var present = active.Where(r => r.MatchKey is not null).Select(r => r.MatchKey!).ToHashSet();
            foreach (var p in primaryRows.Where(p => !p.Excluded && !present.Contains(p.MatchKey!)))
                exceptions.Add(new(primary.Name, p.RowNumber, ExceptionType.MissingInSource, Display(p.MatchKey!),
                    $"{Describe(def, p, source.MatchFields)} has no record in {source.Name}.", p.Amount, false));
        }

        if (source.CompareField is { } cmp && source.PrimaryCompareField is { } pcmp)
        {
            // Compare aggregated per match key so one-to-many splits (e.g. two fuel fills on one trip) reconcile correctly.
            foreach (var g in active.Where(r => !r.Excluded && r.MatchKey is not null).GroupBy(r => r.MatchKey!))
            {
                var candidates = primaryIndex[g.Key].ToList();
                if (candidates.Count == 0) continue;
                var theirs = g.Sum(r => r.Values.GetValueOrDefault(cmp) as decimal? ?? 0m);
                var ours = candidates.Sum(p => p.Values.GetValueOrDefault(pcmp) as decimal? ?? 0m);
                var diff = theirs - ours;
                if (Math.Abs(diff) > source.CompareTolerance)
                    exceptions.Add(new(source.Name, g.First().RowNumber, ExceptionType.AmountMismatch, Display(g.Key),
                        $"{source.Name} {def.Field(cmp)?.Label ?? cmp} {theirs.ToString("N2", CultureInfo.InvariantCulture)} vs " +
                        $"{primary.Name} {def.Field(pcmp)?.Label ?? pcmp} {ours.ToString("N2", CultureInfo.InvariantCulture)} " +
                        $"(difference {diff.ToString("+#,##0.00;-#,##0.00", CultureInfo.InvariantCulture)}).",
                        diff, false));
            }
        }
    }

    private static void Settle(WorkflowDefinition def, RunResult result)
    {
        var lines = new Dictionary<string, SettlementLine>();
        SettlementLine LineFor(string g) => lines.TryGetValue(g, out var l) ? l : lines[g] = new SettlementLine { Group = g };

        foreach (var source in def.Sources)
        {
            var stats = result.Stats[source.Name];
            foreach (var r in result.Rows[source.Name])
            {
                if (r.Excluded || r.Group is null) { stats.RowsExcluded++; continue; }
                stats.RowsIncluded++;
                var line = LineFor(r.Group);
                line.SourceCounts[source.Name] = line.SourceCounts.GetValueOrDefault(source.Name) + 1;
                if (source.AmountField is null || source.Role == SourceRole.ReferenceOnly) continue;
                var amt = r.Amount ?? 0m;
                line.SourceTotals[source.Name] = line.SourceTotals.GetValueOrDefault(source.Name) + amt;
                stats.Total += amt;
                line.Net += source.Role == SourceRole.Deduction ? -amt : amt;
            }
        }

        // Count exceptions per group by looking up the row each exception points at.
        var rowGroup = result.Rows.SelectMany(kv => kv.Value)
            .Where(r => r.Group is not null)
            .ToDictionary(r => (r.Source, r.RowNumber), r => r.Group!);
        foreach (var ex in result.Exceptions.Where(e => e.RowNumber is not null))
            if (rowGroup.TryGetValue((ex.Source, ex.RowNumber!.Value), out var g) && lines.TryGetValue(g, out var line))
                line.ExceptionCount++;

        result.Lines.AddRange(lines.Values.OrderBy(l => l.Group, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Builds a normalized composite key. Parts are joined with a unit separator.</summary>
    internal static string KeyOf(WorkflowDefinition def, NormalizedRow row, IEnumerable<string> fields) =>
        string.Join('\u001F', fields.Select(f =>
        {
            var v = row.Values.GetValueOrDefault(f);
            return v switch
            {
                null => "",
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                decimal m => m.ToString("0.########", CultureInfo.InvariantCulture),
                _ => ValueNormalizer.NormalizeKey(v)
            };
        }));

    public static string Display(string key) => key.Replace('\u001F', '/');

    private static string Describe(WorkflowDefinition def, NormalizedRow row, IEnumerable<string> fields) =>
        string.Join(", ", fields.Select(f =>
        {
            var v = row.Values.GetValueOrDefault(f);
            var s = v is DateOnly d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);
            return $"{def.Field(f)?.Label ?? f} {s}";
        }));
}
