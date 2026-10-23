using System.IO.Compression;

namespace SettleFlow.Core.Parsing;

public sealed record UploadCheck(bool Ok, string? Error, TabularFormat Format)
{
    public static UploadCheck Fail(string error) => new(false, error, default);
}

/// <summary>
/// Defensive checks on uploaded spreadsheets: extension allow-list, size limit, magic bytes,
/// and rejection of macro-bearing or encrypted workbooks.
/// </summary>
public static class UploadValidator
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Workbook parts that can carry active or external content. Data-only uploads never need them.</summary>
    internal static string? RiskyXlsxPart(string entry)
    {
        var e = entry.ToLowerInvariant();
        if (e.StartsWith("xl/embeddings/")) return "embedded files or OLE objects";
        if (e.StartsWith("xl/activex/")) return "ActiveX controls";
        if (e.StartsWith("xl/externallinks/")) return "links to external workbooks";
        if (e.StartsWith("customui/") || e.StartsWith("xl/customui")) return "custom ribbon code";
        if (e.EndsWith(".bin") && !e.StartsWith("xl/printersettings/")) return "binary objects";
        return null;
    }

    /// <summary>Looks for a UTF-16LE stream name inside an OLE (legacy .xls) container's directory.</summary>
    private static bool ContainsOleStream(Stream content, string name)
    {
        var needle = System.Text.Encoding.Unicode.GetBytes(name);
        content.Position = 0;
        var buffer = new byte[content.Length];
        content.ReadExactly(buffer);
        content.Position = 0;
        return buffer.AsSpan().IndexOf(needle) >= 0;
    }

    public static UploadCheck Check(string fileName, Stream content, long maxBytes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".xlsm" or ".xlsb" or ".xltm")
            return UploadCheck.Fail("Macro-enabled or binary workbooks are not accepted. Save as .xlsx or .csv.");
        if (ext is not (".xlsx" or ".xls" or ".csv"))
            return UploadCheck.Fail("Only .xlsx, .xls and .csv files are accepted.");
        if (content.Length == 0) return UploadCheck.Fail("The file is empty.");
        if (content.Length > maxBytes)
            return UploadCheck.Fail($"The file is {content.Length / 1024.0 / 1024.0:N1} MB; the limit is {maxBytes / 1024.0 / 1024.0:N0} MB.");

        var head = new byte[8];
        content.Position = 0;
        var read = content.Read(head, 0, head.Length);
        content.Position = 0;

        switch (ext)
        {
            case ".xlsx":
                if (read < 4 || !head.AsSpan(0, 4).SequenceEqual(ZipMagic))
                    return UploadCheck.Fail("This doesn't look like a valid .xlsx file (it may be encrypted or renamed).");
                try
                {
                    using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
                    if (zip.Entries.Count > 10_000) return UploadCheck.Fail("The workbook has an unusual structure and was rejected.");
                    long expanded = 0;
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                            return UploadCheck.Fail("The workbook contains macros and was rejected.");
                        if (RiskyXlsxPart(e.FullName) is { } risk)
                            return UploadCheck.Fail($"The workbook contains {risk} and was rejected. Copy the data into a new workbook (paste values only) and upload that.");
                        expanded += e.Length;
                    }
                    // Zip-bomb guard: refuse workbooks that expand far beyond their upload size.
                    if (expanded > Math.Max(maxBytes * 20, 200L * 1024 * 1024))
                        return UploadCheck.Fail("The workbook expands to an unsafe size and was rejected.");
                    if (zip.GetEntry("xl/workbook.xml") is null)
                        return UploadCheck.Fail("This .xlsx file is missing its workbook and can't be read.");
                }
                catch (InvalidDataException)
                {
                    return UploadCheck.Fail("This .xlsx file is corrupt.");
                }
                finally { content.Position = 0; }
                return new UploadCheck(true, null, TabularFormat.Xlsx);

            case ".xls":
                if (read < 8 || !head.AsSpan().SequenceEqual(OleMagic))
                    return UploadCheck.Fail("This doesn't look like a valid .xls file.");
                if (ContainsOleStream(content, "_VBA_PROJECT") || ContainsOleStream(content, "Macros"))
                    return UploadCheck.Fail("The workbook contains macros and was rejected. Save it as .xlsx or .csv.");
                if (ContainsOleStream(content, "MBD0"))
                    return UploadCheck.Fail("The workbook contains embedded objects and was rejected. Save it as .xlsx or .csv.");
                return new UploadCheck(true, null, TabularFormat.Xls);

            default: // .csv
                var probe = new byte[Math.Min(content.Length, 64 * 1024)];
                var n = content.Read(probe, 0, probe.Length);
                content.Position = 0;
                if (n >= 4 && (probe.AsSpan(0, 4).SequenceEqual(ZipMagic) || probe.AsSpan(0, 4).SequenceEqual(OleMagic.AsSpan(0, 4))))
                    return UploadCheck.Fail("This file is a binary spreadsheet with a .csv extension. Upload it with its real extension.");
                if (probe.AsSpan(0, n).IndexOf((byte)0) >= 0 && !(n >= 2 && probe[0] == 0xFF && probe[1] == 0xFE))
                    return UploadCheck.Fail("This file contains binary data and isn't a valid CSV.");
                return new UploadCheck(true, null, TabularFormat.Csv);
        }
    }
}
