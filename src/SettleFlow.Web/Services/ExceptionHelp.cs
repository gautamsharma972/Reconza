namespace SettleFlow.Web.Services;

/// <summary>Plain-language explanation and next step for each exception type, shown next to the exceptions list.</summary>
public static class ExceptionHelp
{
    public sealed record Help(string Title, string Meaning, string Action, string Tone);

    private static readonly Dictionary<string, Help> Map = new()
    {
        ["Duplicate"] = new("Duplicate", "The same record appears more than once (same key fields). Only the first copy is counted.",
            "Check whether it is an accidental copy. If both rows are genuine, open the saved check and add Date or Amount to the duplicate check.", "brand"),
        ["Unmatched"] = new("No matching record", "This row refers to something that is not in your main list — for example, a fuel fill for a trip number you do not have.",
            "Check for a typo in the reference, or whether the trip belongs to a different week or region.", "warn"),
        ["MissingInSource"] = new("Missing from another file", "A record in your main list does not appear in this file — for example, a trip the customer did not include in their report.",
            "Check with the person or business that supplied the file before closing the period; it may not have been billed.", "warn"),
        ["AmountMismatch"] = new("Amount mismatch", "Both files have the record, but the amounts differ by more than the allowed tolerance.",
            "Compare the original records and check for partial payments. The Amount column shows the difference.", "warn"),
        ["GroupConflict"] = new("Assigned to different people", "The two files attribute this record to different groups (e.g. different drivers).",
            "Confirm who actually did the work; the source file's own value was used.", "warn"),
        ["InvalidValue"] = new("Unreadable value", "A cell couldn't be read as the expected type — e.g. 'N/A' in an amount column. The row was left out.",
            "Fix the value in the original file and check the files again, or keep it as is if the row should be ignored.", "bad"),
        ["MissingValue"] = new("Required value missing", "A required field is empty, so the row was left out of totals.",
            "Fill in the missing value in the original file and check the files again.", "bad"),
        ["MissingColumn"] = new("Column not found", "A mapped column isn't in the uploaded file — the vendor may have renamed it.",
            "Open this file's setup, load the new file as a sample and connect the renamed column.", "bad"),
        ["Unattributed"] = new("Can't attribute", "The row couldn't be assigned to anyone, so it was left out.",
            "Open this file's setup and connect the identifying column, such as Driver ID.", "bad"),
    };

    public static Help For(string type) => Map.GetValueOrDefault(type) ?? new(type, "", "", "bad");
}
