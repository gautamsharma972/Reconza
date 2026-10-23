using SettleFlow.Core.Parsing;

namespace SettleFlow.Core.Engine;

public sealed record KeyCandidate(string ColumnA, string ColumnB, double Overlap);

public sealed record CompareRow(int RowNumber, string Key, decimal? Amount, IReadOnlyList<string> Cells);

public sealed record AmountDiff(string Key, int RowA, int RowB, decimal AmountA, decimal AmountB)
{
    public decimal Difference => AmountB - AmountA;
}

public sealed class CompareResult
{
    public required string KeyA { get; init; }
    public required string KeyB { get; init; }
    public string? AmountA { get; init; }
    public string? AmountB { get; init; }
    public int RowsA { get; init; }
    public int RowsB { get; init; }
    public int Matched { get; set; }
    public List<CompareRow> OnlyInA { get; } = new();
    public List<CompareRow> OnlyInB { get; } = new();
    public List<CompareRow> DuplicatesA { get; } = new();
    public List<CompareRow> DuplicatesB { get; } = new();
    public List<AmountDiff> AmountDiffs { get; } = new();
    public decimal? TotalA { get; set; }
    public decimal? TotalB { get; set; }
}

/// <summary>
/// Compares two spreadsheets on a key column — the free public tool. Same normalization as the full engine:
/// "T-1001", "t1001" and "T 1001" are the same key.
/// </summary>
public static class FileComparer
{
    /// <summary>Finds the column pair whose values overlap most, preferring columns that look like unique IDs.</summary>
    public static List<KeyCandidate> SuggestKeys(RawTable a, RawTable b, int top = 5)
    {
        static (HashSet<string> Set, double Unique) Profile(RawTable t, int col)
        {
            var values = t.Rows.Take(2000).Select(r => ValueNormalizer.NormalizeKey(r.Cells[col])).Where(v => v.Length > 0).ToList();
            var set = values.ToHashSet();
            return (set, values.Count == 0 ? 0 : set.Count / (double)values.Count);
        }
        var pa = Enumerable.Range(0, Math.Min(a.Headers.Count, 40)).Select(i => (i, p: Profile(a, i))).Where(x => x.p.Set.Count > 1).ToList();
        var pb = Enumerable.Range(0, Math.Min(b.Headers.Count, 40)).Select(i => (i, p: Profile(b, i))).Where(x => x.p.Set.Count > 1).ToList();
        var candidates = new List<KeyCandidate>();
        foreach (var x in pa)
        foreach (var y in pb)
        {
            var inter = x.p.Set.Count(y.p.Set.Contains);
            if (inter == 0) continue;
            var overlap = inter / (double)Math.Min(x.p.Set.Count, y.p.Set.Count);
            // Amount-like columns overlap by coincidence; weight by uniqueness so IDs win.
            var score = overlap * Math.Min(x.p.Unique, y.p.Unique);
            candidates.Add(new(a.Headers[x.i], b.Headers[y.i], Math.Round(score, 3)));
        }
        return candidates.OrderByDescending(c => c.Overlap).Take(top).ToList();
    }

    /// <summary>Picks the most likely amount column (mostly numeric, not the key, not a date).</summary>
    public static string? SuggestAmount(RawTable t, string key)
    {
        string? best = null;
        var bestScore = 0.0;
        for (var i = 0; i < t.Headers.Count; i++)
        {
            if (t.Headers[i].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var cells = t.Rows.Take(500).Select(r => r.Cells[i]).Where(c => !TabularFileReader.IsBlank(c)).ToList();
            if (cells.Count == 0 || cells.Any(c => c is DateTime)) continue;
            // Money columns tend to have decimals or thousands separators; counts and IDs don't.
            var moneyLike = cells.Count(c => ValueNormalizer.TryParseNumber(c, out var n)
                                             && (n != Math.Floor(n) || c is string s && (s.Contains('.') || s.Contains(','))));
            var parsed = cells.Count(c => ValueNormalizer.TryParseNumber(c, out _)) / (double)cells.Count;
            var h = t.Headers[i].ToLowerInvariant();
            var nameBonus = h.Contains("amount") || h.Contains("amt") || h.Contains("total") || h.Contains("value") || h.Contains("price") || h.Contains("freight") ? 0.5 : 0;
            var score = parsed < 0.9 ? 0 : parsed + nameBonus + moneyLike / (double)cells.Count * 0.3;
            if (score > bestScore) { bestScore = score; best = t.Headers[i]; }
        }
        return best;
    }

    public static CompareResult Compare(RawTable a, RawTable b, string keyA, string keyB, string? amountA, string? amountB, decimal tolerance = 0.01m)
    {
        var result = new CompareResult
        {
            KeyA = keyA, KeyB = keyB, AmountA = amountA, AmountB = amountB, RowsA = a.Rows.Count, RowsB = b.Rows.Count,
        };
        var rowsA = Index(a, keyA, amountA, result.DuplicatesA);
        var rowsB = Index(b, keyB, amountB, result.DuplicatesB);

        foreach (var (key, ra) in rowsA)
        {
            if (!rowsB.TryGetValue(key, out var rb)) { result.OnlyInA.Add(ra); continue; }
            result.Matched++;
            if (ra.Amount is { } x && rb.Amount is { } y && Math.Abs(x - y) > tolerance)
                result.AmountDiffs.Add(new(ra.Key, ra.RowNumber, rb.RowNumber, x, y));
        }
        result.OnlyInB.AddRange(rowsB.Where(kv => !rowsA.ContainsKey(kv.Key)).Select(kv => kv.Value));
        if (amountA is not null) result.TotalA = rowsA.Values.Sum(r => r.Amount ?? 0);
        if (amountB is not null) result.TotalB = rowsB.Values.Sum(r => r.Amount ?? 0);
        return result;
    }

    private static Dictionary<string, CompareRow> Index(RawTable t, string key, string? amount, List<CompareRow> duplicates)
    {
        var map = new Dictionary<string, CompareRow>();
        foreach (var r in t.Rows)
        {
            var raw = t.Get(r, key);
            var k = ValueNormalizer.NormalizeKey(raw);
            if (k.Length == 0) continue;
            decimal? amt = amount is not null && ValueNormalizer.TryParseNumber(t.Get(r, amount), out var n) ? n : null;
            var row = new CompareRow(r.RowNumber, ValueNormalizer.NormalizeText(raw), amt,
                r.Cells.Take(8).Select(ValueNormalizer.NormalizeText).ToList());
            if (!map.TryAdd(k, row)) duplicates.Add(row);
        }
        return map;
    }
}
