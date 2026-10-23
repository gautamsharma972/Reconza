using System.Text;
using ExcelDataReader;

namespace SettleFlow.Core.Parsing;

public sealed record RawRow(int RowNumber, object?[] Cells);

public sealed class RawTable
{
    public required string SheetName { get; init; }
    public required List<string> Headers { get; init; }
    public required List<RawRow> Rows { get; init; }
    public List<string> SheetNames { get; init; } = new();

    public object? Get(RawRow row, string header)
    {
        var i = Headers.FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
        return i < 0 || i >= row.Cells.Length ? null : row.Cells[i];
    }
}

public enum TabularFormat { Xlsx, Xls, Csv }

/// <summary>Reads XLSX/XLS/CSV into a header + rows table, tolerating title rows above the header.</summary>
public static class TabularFileReader
{
    static TabularFileReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public const int HeaderScanRows = 15;

    public static TabularFormat FormatFromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" => TabularFormat.Xlsx,
            ".xls" => TabularFormat.Xls,
            ".csv" or ".txt" => TabularFormat.Csv,
            var ext => throw new NotSupportedException($"Unsupported file type '{ext}'.")
        };

    public static RawTable Read(Stream stream, TabularFormat format, string? sheetName = null, int maxRows = 200_000)
    {
        using var reader = format == TabularFormat.Csv
            ? ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration { FallbackEncoding = Encoding.UTF8 })
            : ExcelReaderFactory.CreateReader(stream);

        var sheetNames = new List<string>();
        List<object?[]>? chosen = null;
        string chosenName = "";
        do
        {
            sheetNames.Add(string.IsNullOrEmpty(reader.Name) ? (format == TabularFormat.Csv ? "CSV" : $"Sheet{sheetNames.Count + 1}") : reader.Name);
            var wanted = sheetName is null ? chosen is null : string.Equals(reader.Name, sheetName, StringComparison.OrdinalIgnoreCase);
            if (!wanted) continue;
            chosenName = sheetNames[^1];
            chosen = new List<object?[]>();
            while (reader.Read())
            {
                if (chosen.Count >= maxRows + HeaderScanRows)
                    throw new InvalidDataException($"File exceeds the {maxRows:N0} row limit for your plan.");
                var cells = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++) cells[i] = reader.GetValue(i);
                chosen.Add(cells);
            }
        } while (reader.NextResult());

        if (chosen is null)
            throw new InvalidDataException($"Worksheet '{sheetName}' was not found. Available: {string.Join(", ", sheetNames)}.");

        return Build(chosen, chosenName, sheetNames);
    }

    private static RawTable Build(List<object?[]> raw, string sheetName, List<string> sheetNames)
    {
        // Pick the header row: the row (within the first few) with the most non-empty text cells.
        var headerIndex = -1;
        var best = 0;
        for (var r = 0; r < Math.Min(HeaderScanRows, raw.Count); r++)
        {
            var textCells = raw[r].Count(c => c is string s && !string.IsNullOrWhiteSpace(s));
            if (textCells > best) { best = textCells; headerIndex = r; }
        }
        if (headerIndex < 0) return new RawTable { SheetName = sheetName, Headers = new(), Rows = new(), SheetNames = sheetNames };

        var headerCells = raw[headerIndex];
        var headers = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headerCells.Length; i++)
        {
            var h = Convert.ToString(headerCells[i])?.Trim();
            h = string.IsNullOrEmpty(h) ? $"Column {i + 1}" : CollapseWhitespace(h);
            if (seen.TryGetValue(h, out var n)) { seen[h] = n + 1; h = $"{h} ({n + 1})"; }
            else seen[h] = 1;
            headers.Add(h);
        }

        var rows = new List<RawRow>();
        for (var r = headerIndex + 1; r < raw.Count; r++)
        {
            var cells = raw[r];
            if (cells.All(IsBlank)) continue;
            var padded = new object?[headers.Count];
            Array.Copy(cells, padded, Math.Min(cells.Length, headers.Count));
            rows.Add(new RawRow(r + 1, padded)); // 1-based spreadsheet row number
        }

        // Drop trailing auto-named columns that are entirely empty.
        while (headers.Count > 0 && headers[^1].StartsWith("Column ", StringComparison.Ordinal)
               && rows.All(row => IsBlank(row.Cells[headers.Count - 1])))
        {
            headers.RemoveAt(headers.Count - 1);
            for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { Cells = rows[i].Cells[..headers.Count] };
        }

        return new RawTable { SheetName = sheetName, Headers = headers, Rows = rows, SheetNames = sheetNames };
    }

    internal static bool IsBlank(object? v) => v is null or DBNull || (v is string s && string.IsNullOrWhiteSpace(s));

    internal static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
