using SettleFlow.Core.Parsing;

namespace SettleFlow.Web.Services;

public sealed record InspectionResult(bool Ok, string? Error, TabularFormat Format, ScanResult Scan);

/// <summary>Every uploaded file goes through here: structural checks first (cheap), then antivirus.</summary>
public sealed class UploadInspector(IMalwareScanner scanner, AuditLog audit)
{
    public async Task<InspectionResult> InspectAsync(string fileName, byte[] content, long maxBytes, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(content, writable: false);
        var check = UploadValidator.Check(fileName, ms, maxBytes);
        if (!check.Ok) return new(false, check.Error, default, new(ScanVerdict.NotScanned, "Rejected before scanning"));

        var scan = await scanner.ScanAsync(content, ct);
        if (scan.Verdict == ScanVerdict.Infected)
        {
            audit.Record("upload.blocked", $"'{Path.GetFileName(fileName)}' blocked by antivirus: {scan.Detail}.");
            return new(false, $"The file was blocked by the virus scanner ({scan.Detail}).", check.Format, scan);
        }
        if (!scan.Allowed(scanner.Mode))
            return new(false, "Uploads are paused because the virus scanner is unavailable. Please try again in a few minutes.", check.Format, scan);
        return new(true, null, check.Format, scan);
    }
}
