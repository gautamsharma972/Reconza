namespace SettleFlow.Web.Services;

/// <summary>Plain-language explanation and next step for each exception type, shown next to the exceptions list.</summary>
public static class ExceptionHelp
{
    public sealed record Help(string Title, string Meaning, string Action, string Tone);

    private static readonly Dictionary<string, Help> Map = new()
    {
        ["Duplicate"] = new("Duplicate", "The same record appears more than once (same key fields). Only the first copy is counted.",
            "Usually safe to accept. If both copies are genuine (e.g. two fills on the same trip), add a field like Date or Amount to the source's unique key.", "brand"),
        ["Unmatched"] = new("No matching record", "This row refers to something that isn't in your system-of-record file — e.g. a fuel fill for a trip number you don't have.",
            "Check for a typo in the reference, or whether the trip belongs to a different week or region.", "warn"),
        ["MissingInSource"] = new("Missing from another file", "A record in your system of record has no counterpart in this file — e.g. a trip the customer didn't include in their report.",
            "Ask the counterparty about it before closing the period; it may be unbilled.", "warn"),
        ["AmountMismatch"] = new("Amount mismatch", "Both files have the record, but the amounts differ by more than the allowed tolerance.",
            "Compare rate cards or check for partial payments and short-pays. The Amount column shows the difference.", "warn"),
        ["GroupConflict"] = new("Assigned to different people", "The two files attribute this record to different groups (e.g. different drivers).",
            "Confirm who actually did the work; the source file's own value was used.", "warn"),
        ["InvalidValue"] = new("Unreadable value", "A cell couldn't be read as the expected type — e.g. 'N/A' in an amount column. The row was left out.",
            "Fix the value in the source file and re-run, or accept if the row should be ignored.", "bad"),
        ["MissingValue"] = new("Required value missing", "A required field is empty, so the row was left out of totals.",
            "Fill in the missing value at the source and re-run.", "bad"),
        ["MissingColumn"] = new("Column not found", "A mapped column isn't in the uploaded file — the vendor may have renamed it.",
            "Open the source's mapping, load this file as a sample and re-map the column.", "bad"),
        ["Unattributed"] = new("Can't attribute", "The row couldn't be assigned to anyone, so it was left out.",
            "Map the group field (e.g. Driver ID) on this source or set match fields.", "bad"),
    };

    public static Help For(string type) => Map.GetValueOrDefault(type) ?? new(type, "", "", "bad");
}
