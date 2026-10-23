using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using SettleFlow.Core.Engine;
using SettleFlow.Core.Parsing;
using SettleFlow.Core.Presets;
using SettleFlow.Core.Reporting;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Site.Tools;

/// <summary>
/// Free, no-sign-up comparison of two spreadsheets. Files are kept only in server memory for 15 minutes
/// (so the visitor can change columns or download the result) and are never written to disk or the database.
/// </summary>
[EnableRateLimiting("tool")]
public sealed class CompareExcelModel(IMemoryCache cache, IMalwareScanner scanner, ILogger<CompareExcelModel> log) : PageModel
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxRows = 5_000;
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(15);

    private sealed record Pair(string NameA, byte[] A, string NameB, byte[] B);

    [BindProperty] public string? Token { get; set; }
    [BindProperty] public string? KeyA { get; set; }
    [BindProperty] public string? KeyB { get; set; }
    [BindProperty] public string? AmountA { get; set; }
    [BindProperty] public string? AmountB { get; set; }

    public string? Error { get; private set; }
    public CompareResult? Result { get; private set; }
    public List<string> HeadersA { get; private set; } = new();
    public List<string> HeadersB { get; private set; } = new();
    public string NameA { get; private set; } = "";
    public string NameB { get; private set; } = "";

    public void OnGet() { }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? fileA, IFormFile? fileB, CancellationToken ct)
    {
        if (fileA is null || fileB is null) { Error = "Choose two files to compare."; return Page(); }
        var a = await ReadAsync(fileA, ct);
        var b = await ReadAsync(fileB, ct);
        if (Error is not null) return Page();
        Token = Remember(new Pair(Path.GetFileName(fileA.FileName), a!, Path.GetFileName(fileB.FileName), b!));
        KeyA = KeyB = AmountA = AmountB = null; // auto-detect
        return Run();
    }

    public IActionResult OnPostSample()
    {
        var files = SampleData.DriverSettlement();
        var a = files.Single(f => f.FileName == "trips.xlsx");
        var b = files.Single(f => f.FileName == "customer_shipments.xlsx");
        Token = Remember(new Pair(a.FileName, a.Content, b.FileName, b.Content));
        return Run();
    }

    public IActionResult OnPostRecompare() => Run();

    public IActionResult OnPostDownload()
    {
        if (Run() is not PageResult || Result is null) return Page();
        using var ms = new MemoryStream();
        ComparisonReportWriter.Write(Result, NameA, NameB, ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "comparison-reconza.xlsx");
    }

    private IActionResult Run()
    {
        if (Token is null || !cache.TryGetValue<Pair>("cmp:" + Token, out var pair) || pair is null)
        {
            Error = "Your files have expired (we only keep them for 15 minutes). Please upload them again.";
            return Page();
        }
        NameA = pair.NameA; NameB = pair.NameB;
        try
        {
            var ta = TabularFileReader.Read(new MemoryStream(pair.A), TabularFileReader.FormatFromFileName(pair.NameA), null, MaxRows);
            var tb = TabularFileReader.Read(new MemoryStream(pair.B), TabularFileReader.FormatFromFileName(pair.NameB), null, MaxRows);
            HeadersA = ta.Headers; HeadersB = tb.Headers;
            if (ta.Headers.Count == 0 || tb.Headers.Count == 0) { Error = "One of the files has no header row we could read."; return Page(); }

            if (!ta.Headers.Contains(KeyA ?? "") || !tb.Headers.Contains(KeyB ?? ""))
            {
                var best = FileComparer.SuggestKeys(ta, tb).FirstOrDefault();
                if (best is null) { Error = "We couldn't find a column with matching values in both files. Choose the key columns below."; KeyA = ta.Headers[0]; KeyB = tb.Headers[0]; }
                else { KeyA = best.ColumnA; KeyB = best.ColumnB; }
                AmountA = FileComparer.SuggestAmount(ta, KeyA);
                AmountB = FileComparer.SuggestAmount(tb, KeyB);
            }
            if (!ta.Headers.Contains(AmountA ?? "")) AmountA = null;
            if (!tb.Headers.Contains(AmountB ?? "")) AmountB = null;
            Result = FileComparer.Compare(ta, tb, KeyA!, KeyB!, AmountA, AmountB);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ExcelDataReader.Exceptions.ExcelReaderException)
        {
            Error = ex.Message;
        }
        ModelState.Clear();
        return Page();
    }

    private async Task<byte[]?> ReadAsync(IFormFile f, CancellationToken ct)
    {
        if (Error is not null) return null;
        if (f.Length > MaxBytes) { Error = $"'{f.FileName}' is larger than 2 MB. Create a free account to check larger files."; return null; }
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms, ct);
        var check = UploadValidator.Check(f.FileName, ms, MaxBytes);
        if (!check.Ok) { Error = $"{f.FileName}: {check.Error}"; return null; }
        var bytes = ms.ToArray();
        var scan = await scanner.ScanAsync(bytes, ct);
        if (scan.Verdict == ScanVerdict.Infected || !scan.Allowed(scanner.Mode))
        {
            log.LogWarning("Free tool upload rejected by scanner: {Detail}", scan.Detail);
            Error = $"{f.FileName} was rejected by the virus scanner.";
            return null;
        }
        return bytes;
    }

    private string Remember(Pair pair)
    {
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        cache.Set("cmp:" + token, pair, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Keep, Size = pair.A.Length + pair.B.Length });
        return token;
    }
}
