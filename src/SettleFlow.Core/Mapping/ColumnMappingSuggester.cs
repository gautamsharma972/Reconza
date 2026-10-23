using System.Text.RegularExpressions;
using SettleFlow.Core.Model;

namespace SettleFlow.Core.Mapping;

public sealed record MappingSuggestion(string Field, string? Column, double Confidence, string Reason, string Origin = "Rules");

public sealed record SuggestionResult(IReadOnlyList<MappingSuggestion> Suggestions, string? Notice = null);

/// <summary>
/// Suggests column → field mappings from header names and sample values.
/// Suggestions are only ever pre-filled for the user to confirm; nothing runs on an unconfirmed mapping.
/// </summary>
public interface IMappingSuggester
{
    Task<SuggestionResult> SuggestAsync(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken ct = default);
}

/// <summary>Deterministic, offline suggester based on header synonyms and value shapes.</summary>
public sealed partial class HeuristicMappingSuggester : IMappingSuggester
{
    public Task<SuggestionResult> SuggestAsync(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken ct = default) =>
        Task.FromResult(new SuggestionResult(Suggest(fields, headers, sampleRows)));

    public static IReadOnlyList<MappingSuggestion> Suggest(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows)
    {
        var scored = new List<(FieldDef Field, int Col, double Score, string Reason)>();
        foreach (var f in fields)
            for (var c = 0; c < headers.Count; c++)
            {
                var (score, reason) = ScoreHeader(f, headers[c]);
                var shape = ShapeBonus(f.Type, sampleRows.Select(r => c < r.Count ? r[c] : "").ToList());
                score = score <= 0 ? 0 : Math.Clamp(score + shape, 0, 1);
                if (score > 0.3) scored.Add((f, c, score, reason));
            }

        // Greedy one-to-one assignment, best scores first.
        var usedCols = new HashSet<int>();
        var assigned = new Dictionary<string, MappingSuggestion>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scored.OrderByDescending(s => s.Score))
        {
            if (assigned.ContainsKey(s.Field.Name) || usedCols.Contains(s.Col)) continue;
            assigned[s.Field.Name] = new MappingSuggestion(s.Field.Name, headers[s.Col], Math.Round(s.Score, 2), s.Reason);
            usedCols.Add(s.Col);
        }
        return fields.Select(f => assigned.GetValueOrDefault(f.Name) ?? new MappingSuggestion(f.Name, null, 0, "No similar column")).ToList();
    }

    private static (double, string) ScoreHeader(FieldDef f, string header)
    {
        var h = Norm(header);
        if (h.Length == 0) return (0, "");
        var candidates = f.Synonyms.Append(f.Name).Append(f.Label).Select(Norm).Where(x => x.Length > 0).Distinct().ToList();
        if (candidates.Contains(h)) return (0.95, "Header matches a known name");

        var best = 0.0; var reason = "";
        var hTokens = Tokens(header);
        foreach (var cand in f.Synonyms.Append(f.Name).Append(f.Label))
        {
            var cn = Norm(cand);
            if (cn.Length >= 3 && (h.Contains(cn) || (h.Length >= 3 && cn.Contains(h))))
            {
                var s = 0.6 + 0.25 * Math.Min(cn.Length, h.Length) / Math.Max(cn.Length, h.Length);
                if (s > best) { best = s; reason = $"Header contains '{cand}'"; }
            }
            var cTokens = Tokens(cand);
            if (cTokens.Count > 0 && hTokens.Count > 0)
            {
                var overlap = cTokens.Intersect(hTokens).Count() / (double)cTokens.Union(hTokens).Count();
                if (overlap * 0.8 > best) { best = overlap * 0.8; reason = $"Header shares words with '{cand}'"; }
            }
        }
        return (best, reason);
    }

    /// <summary>+0.05 when sample values fit the field type, -0.3 when they clearly don't.</summary>
    public static double ShapeBonus(FieldType type, List<string> values)
    {
        var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (nonEmpty.Count == 0) return 0;
        var numeric = nonEmpty.Count(v => Parsing.ValueNormalizer.TryParseNumber(v, out _)) / (double)nonEmpty.Count;
        var dates = nonEmpty.Count(v => DateLike().IsMatch(v)) / (double)nonEmpty.Count;
        return type switch
        {
            FieldType.Number => numeric > 0.8 ? 0.05 : -0.3,
            FieldType.Date => dates > 0.8 ? 0.05 : -0.3,
            _ => 0
        };
    }

    private static string Norm(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");
    private static HashSet<string> Tokens(string s) =>
        WordSplit().Split(CamelSplit().Replace(s, "$1 $2").ToLowerInvariant()).Where(t => t.Length > 1 && t is not ("no" or "of" or "the")).ToHashSet();

    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex WordSplit();
    [GeneratedRegex("([a-z])([A-Z])")] private static partial Regex CamelSplit();
    [GeneratedRegex(@"^\d{1,4}[-/.\s]\w{1,9}[-/.\s,]+\d{2,4}|^\d{4}-\d{2}-\d{2}")] private static partial Regex DateLike();
}
