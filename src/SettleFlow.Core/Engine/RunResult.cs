using SettleFlow.Core.Model;

namespace SettleFlow.Core.Engine;

public enum ExceptionType
{
    MissingColumn,
    MissingValue,
    InvalidValue,
    Duplicate,
    Unmatched,
    MissingInSource,
    AmountMismatch,
    GroupConflict,
    Unattributed,
}

public sealed record ReconException(
    string Source,
    int? RowNumber,
    ExceptionType Type,
    string Key,
    string Message,
    decimal? Amount,
    bool ExcludedFromTotals);

public sealed class NormalizedRow
{
    public required string Source { get; init; }
    public required int RowNumber { get; init; }
    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Valid { get; set; } = true;
    public bool Excluded { get; set; }
    public string? Group { get; set; }
    public string? MatchKey { get; set; }
    public decimal? Amount { get; set; }
}

public sealed class SourceStats
{
    public int RowsRead { get; set; }
    public int RowsIncluded { get; set; }
    public int RowsExcluded { get; set; }
    public decimal Total { get; set; }
}

public sealed class SettlementLine
{
    public required string Group { get; init; }
    public Dictionary<string, decimal> SourceTotals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> SourceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public decimal Net { get; set; }
    public int ExceptionCount { get; set; }
}

public sealed class RunResult
{
    public required WorkflowDefinition Definition { get; init; }
    public List<SettlementLine> Lines { get; } = new();
    public List<ReconException> Exceptions { get; } = new();
    public Dictionary<string, List<NormalizedRow>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SourceStats> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public decimal NetTotal => Lines.Sum(l => l.Net);
}
