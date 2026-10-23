using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SettleFlow.Core.Mapping;
using SettleFlow.Core.Parsing;
using SettleFlow.Core.Presets;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public class HybridMappingTests
{
    private static readonly string[] Headers = ["Chauffeur Cd", "Consgt #", "Dt of Dispatch", "Hire Chgs (INR)", "Remarks"];
    private static readonly List<IReadOnlyList<string>> Rows = [["D001", "LR-1", "2026-10-12", "8,450.00", "ok"], ["D002", "LR-2", "2026-10-13", "9,120.00", ""]];

    [Fact]
    public void Model_output_is_validated_before_use()
    {
        var def = WorkflowPresets.DriverSettlement();
        var rules = HeuristicMappingSuggester.Suggest(def.Fields, Headers, Rows);
        List<HybridMappingSuggester.LlmMapping> llm =
        [
            new("DriverId", "Chauffeur Cd", 0.9, null),
            new("TripRef", "Consignment Number", 0.9, null), // hallucinated header
            new("Amount", "Consgt #", 0.9, null),            // text column for a Number field
            new("Date", "Dt of Dispatch", 0.8, null),
            new("Litres", "Chauffeur Cd", 0.95, null),       // column already used
            new("Description", "Hire Chgs (INR)", 0.9, null), // exact rule match "Remarks" must win
        ];
        var merged = HybridMappingSuggester.Merge(def.Fields, Headers, Rows, rules, llm, "test-model").ToDictionary(m => m.Field);

        Assert.Equal("Chauffeur Cd", merged["DriverId"].Column);
        Assert.Equal("AI", merged["DriverId"].Origin);
        Assert.Null(merged["TripRef"].Column);
        Assert.Null(merged["Amount"].Column);
        Assert.Equal("Dt of Dispatch", merged["Date"].Column);
        Assert.Null(merged["Litres"].Column);
        Assert.Equal("Remarks", merged["Description"].Column);
        Assert.Equal("Rules", merged["Description"].Origin);
        Assert.All(merged.Values, m => Assert.True(m.Confidence <= 0.95));
    }

    [Fact]
    public async Task Unreachable_ollama_falls_back_to_rules()
    {
        var def = WorkflowPresets.DriverSettlement();
        var s = new HybridMappingSuggester(new HttpClient(), new OllamaOptions { Enabled = true, BaseUrl = "http://127.0.0.1:1", TimeoutSeconds = 5 });
        var r = await s.SuggestAsync(def.Fields, ["Load Ref", "Amount"], [["T1", "10"]]);
        Assert.NotNull(r.Notice);
        Assert.Equal("Load Ref", r.Suggestions.Single(x => x.Field == "TripRef").Column);
    }

    /// <summary>Live check against a local Ollama. Set OLLAMA_TEST=1 to run.</summary>
    [Fact]
    public async Task Live_ollama_maps_cryptic_headers()
    {
        if (Environment.GetEnvironmentVariable("OLLAMA_TEST") != "1") return;
        var def = WorkflowPresets.DriverSettlement();
        var s = new HybridMappingSuggester(new HttpClient(), new OllamaOptions { Enabled = true });
        var r = await s.SuggestAsync(def.Fields, Headers, Rows);
        Assert.Null(r.Notice);
        Assert.Equal("Consgt #", r.Suggestions.Single(x => x.Field == "TripRef").Column);
    }
}

public class ScanningTests
{
    /// <summary>Minimal clamd INSTREAM server that flags payloads containing the EICAR marker.</summary>
    private static (int Port, Task Server) FakeClamd(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(ct);
                    await using var s = client.GetStream();
                    var cmd = new List<byte>();
                    int b;
                    while ((b = s.ReadByte()) > 0) cmd.Add((byte)b);
                    var command = Encoding.ASCII.GetString(cmd.ToArray());
                    if (command == "zVERSION") { await s.WriteAsync(Encoding.ASCII.GetBytes("ClamAV 1.4.0\0"), ct); continue; }
                    using var payload = new MemoryStream();
                    var len = new byte[4];
                    while (true)
                    {
                        await s.ReadExactlyAsync(len, ct);
                        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
                        if (n == 0) break;
                        var chunk = new byte[n];
                        await s.ReadExactlyAsync(chunk, ct);
                        payload.Write(chunk);
                    }
                    var infected = Encoding.ASCII.GetString(payload.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");
                    await s.WriteAsync(Encoding.ASCII.GetBytes(infected ? "stream: Eicar-Test-Signature FOUND\0" : "stream: OK\0"), ct);
                }
            }
            catch (OperationCanceledException) { }
            finally { listener.Stop(); }
        }, ct);
        return (port, server);
    }

    [Fact]
    public async Task ClamAv_protocol_clean_and_infected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (port, _) = FakeClamd(cts.Token);
        var scanner = new ClamAvScanner(new ScanOptions { Mode = ScanMode.Required, ClamAvPort = port }, NullLogger<ClamAvScanner>.Instance);

        Assert.Equal(ScanVerdict.Clean, (await scanner.ScanAsync(new byte[200_000])).Verdict); // multi-chunk payload
        var eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var infected = await scanner.ScanAsync(eicar);
        Assert.Equal(ScanVerdict.Infected, infected.Verdict);
        Assert.Equal("Eicar-Test-Signature", infected.Detail);
        Assert.StartsWith("Ready", await scanner.HealthAsync());
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Required_mode_fails_closed_when_scanner_is_down()
    {
        var required = new ClamAvScanner(new ScanOptions { Mode = ScanMode.Required, ClamAvPort = 1 }, NullLogger<ClamAvScanner>.Instance);
        var r = await required.ScanAsync([1, 2, 3]);
        Assert.Equal(ScanVerdict.NotScanned, r.Verdict);
        Assert.False(r.Allowed(ScanMode.Required));
        Assert.True(r.Allowed(ScanMode.Optional));
    }

    private static byte[] XlsxWith(string extraEntry)
    {
        var ms = new MemoryStream();
        ms.Write(SampleData.DriverSettlement()[0].Content);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
            using (var w = new StreamWriter(zip.CreateEntry(extraEntry).Open())) w.Write("x");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("xl/embeddings/oleObject1.bin", "embedded")]
    [InlineData("xl/externalLinks/externalLink1.xml", "external")]
    [InlineData("xl/activeX/activeX1.xml", "ActiveX")]
    [InlineData("xl/vbaProject.bin", "macros")]
    public void Risky_workbook_parts_are_rejected(string entry, string expected)
    {
        var check = UploadValidator.Check("x.xlsx", new MemoryStream(XlsxWith(entry)), 10_000_000);
        Assert.False(check.Ok);
        Assert.Contains(expected, check.Error);
    }

    [Fact]
    public void Printer_settings_are_not_flagged() =>
        Assert.True(UploadValidator.Check("x.xlsx", new MemoryStream(XlsxWith("xl/printerSettings/printerSettings1.bin")), 10_000_000).Ok);
}
