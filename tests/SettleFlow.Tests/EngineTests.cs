using SettleFlow.Core.Engine;
using SettleFlow.Core.Mapping;
using SettleFlow.Core.Model;
using SettleFlow.Core.Parsing;
using SettleFlow.Core.Presets;
using SettleFlow.Core.Reporting;

namespace SettleFlow.Tests;

public class EngineTests
{
    private static RunResult RunSample()
    {
        var def = WorkflowPresets.DriverSettlement();
        var tables = SampleData.DriverSettlement().ToDictionary(
            f => f.SourceName,
            f => TabularFileReader.Read(new MemoryStream(f.Content), TabularFileReader.FormatFromFileName(f.FileName)));
        return ReconciliationEngine.Run(def, tables);
    }

    private static int Count(RunResult r, string source, ExceptionType t) => r.Exceptions.Count(e => e.Source == source && e.Type == t);

    [Fact]
    public void Preset_is_valid() => Assert.Empty(WorkflowPresets.DriverSettlement().Validate());

    [Fact]
    public void Reader_skips_title_rows_and_finds_header()
    {
        var f = SampleData.DriverSettlement().First(f => f.SourceName == "Trips");
        var t = TabularFileReader.Read(new MemoryStream(f.Content), TabularFormat.Xlsx);
        Assert.Equal("Trip No", t.Headers[0]);
        Assert.Equal(21, t.Rows.Count);
        Assert.Equal(4, t.Rows[0].RowNumber);
    }

    [Fact]
    public void Sample_run_flags_every_planted_problem()
    {
        var r = RunSample();
        Assert.Equal(1, Count(r, "Trips", ExceptionType.Duplicate));
        Assert.Equal(1, Count(r, "Trips", ExceptionType.MissingValue));
        Assert.Equal(1, Count(r, "Fuel", ExceptionType.Duplicate));
        Assert.Equal(1, Count(r, "Fuel", ExceptionType.Unmatched));
        Assert.Equal(1, Count(r, "Fuel", ExceptionType.InvalidValue));
        Assert.Equal(1, Count(r, "Deductions", ExceptionType.Duplicate));
        Assert.Single(r.Exceptions, e => e.Type == ExceptionType.AmountMismatch && e.Key == "T1004" && e.Amount == -1250m);
        Assert.Single(r.Exceptions, e => e.Type == ExceptionType.MissingInSource && e.Key == "T1015");
        Assert.Single(r.Exceptions, e => e.Source == "Customer report" && e.Type == ExceptionType.Unmatched && e.Key == "T1018");
        // An unmatched row yields exactly one exception, and it says whether it counts.
        Assert.DoesNotContain(r.Exceptions, e => e.Type == ExceptionType.Unattributed);
        Assert.True(r.Exceptions.Single(e => e.Source == "Fuel" && e.Type == ExceptionType.Unmatched).ExcludedFromTotals);
        Assert.Equal(11 - 2, r.Exceptions.Count);
    }

    [Fact]
    public void Settlement_totals_are_exact()
    {
        var r = RunSample();
        Assert.Equal(5, r.Lines.Count);
        foreach (var line in r.Lines)
            Assert.Equal(line.SourceTotals.GetValueOrDefault("Trips") - line.SourceTotals.GetValueOrDefault("Fuel") - line.SourceTotals.GetValueOrDefault("Deductions"), line.Net);
        // "d001 " and "D-004" normalize onto the same drivers as the trips file.
        Assert.Equal(2000m, r.Lines.Single(l => l.Group == "D001").SourceTotals["Deductions"]);
        Assert.Equal(3000m, r.Lines.Single(l => l.Group == "D004").SourceTotals["Deductions"]);
        Assert.Equal(1050m, r.Lines.Single(l => l.Group == "D002").SourceTotals["Deductions"]);
        // 19 valid trips (20 minus the one missing a driver; the duplicate is excluded).
        Assert.Equal(19, r.Stats["Trips"].RowsIncluded);
        // T-1003 has two fills (plus an excluded duplicate); all attribute to D003.
        Assert.Equal(3, r.Lines.Single(l => l.Group == "D003").SourceCounts["Fuel"]); // T-1003 (2 fills) + T-1013
    }

    [Theory]
    [InlineData("₹1,234.50", 1234.50)]
    [InlineData("(250.00)", -250)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,23,456.00", 123456)]
    [InlineData("$ 99", 99)]
    [InlineData("100-", -100)]
    public void Numbers_parse(string input, double expected)
    {
        Assert.True(ValueNormalizer.TryParseNumber(input, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Theory]
    [InlineData("13/10/2026", "dd/MM/yyyy", "2026-10-13")]
    [InlineData("10/13/2026", "MM/dd/yyyy", "2026-10-13")]
    [InlineData("13-Oct-2026", null, "2026-10-13")]
    [InlineData("46308", null, "2026-10-13")]
    public void Dates_parse(string input, string? fmt, string expected)
    {
        Assert.True(ValueNormalizer.TryParseDate(input, fmt is null ? [] : [fmt], out var d));
        Assert.Equal(DateOnly.Parse(expected), d);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "'=HYPERLINK(\"x\")")]
    [InlineData("+1", "'+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("Normal", "Normal")]
    public void Report_text_is_injection_safe(string input, string expected) => Assert.Equal(expected, ExcelReportWriter.SafeText(input));

    [Fact]
    public void Report_writes_and_reopens()
    {
        var r = RunSample();
        using var ms = new MemoryStream();
        ExcelReportWriter.Write(r, new ReportContext("Demo", "Run 1", DateTimeOffset.UtcNow, "test", new Dictionary<string, string>()), ms);
        ms.Position = 0;
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        Assert.Contains(wb.Worksheets, w => w.Name == "Settlement");
        Assert.Contains(wb.Worksheets, w => w.Name == "Exceptions");
    }

    [Fact]
    public void Suggester_maps_vendor_headers()
    {
        var def = WorkflowPresets.DriverSettlement();
        var s = HeuristicMappingSuggester.Suggest(def.Fields, ["Card No", "Vehicle", "Load Ref", "Txn Date", "Station", "Litres", "Amount"],
            [["4111", "MH12", "t1001", "12/10/2026", "HP", "80", "₹7,712.00"]]);
        Assert.Equal("Load Ref", s.Single(x => x.Field == "TripRef").Column);
        Assert.Equal("Txn Date", s.Single(x => x.Field == "Date").Column);
        Assert.Equal("Amount", s.Single(x => x.Field == "Amount").Column);
        Assert.Equal("Litres", s.Single(x => x.Field == "Litres").Column);
    }

    [Fact]
    public void Upload_validator_rejects_bad_files()
    {
        Assert.False(UploadValidator.Check("x.xlsm", new MemoryStream([1]), 1000).Ok);
        Assert.False(UploadValidator.Check("x.xlsx", new MemoryStream("hello"u8.ToArray()), 1000).Ok);
        Assert.False(UploadValidator.Check("x.csv", new MemoryStream(new byte[2000]), 1000).Ok);
        Assert.True(UploadValidator.Check("x.csv", new MemoryStream("a,b\n1,2"u8.ToArray()), 1000).Ok);
        var xlsx = SampleData.DriverSettlement()[0].Content;
        Assert.True(UploadValidator.Check("x.xlsx", new MemoryStream(xlsx), 10_000_000).Ok);
    }

    [Fact]
    public void Invalid_config_is_rejected()
    {
        var def = WorkflowPresets.DriverSettlement();
        def.Sources[0].Role = SourceRole.Addition;
        Assert.Throws<ReconciliationInputException>(() => ReconciliationEngine.Run(def, new Dictionary<string, RawTable>()));
    }
}
