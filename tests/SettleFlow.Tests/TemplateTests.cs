using ClosedXML.Excel;
using SettleFlow.Core.Reporting;

namespace SettleFlow.Tests;

public class TemplateTests
{
    [Fact]
    public void Driver_settlement_template_formulas_compute_correctly()
    {
        using var wb = new XLWorkbook(new MemoryStream(SettlementTemplate.DriverSettlement()));
        wb.RecalculateAllFormulas();
        var s = wb.Worksheet("Settlement");
        decimal N(string cell) => s.Cell(cell).GetValue<decimal>();

        Assert.Equal("D001", s.Cell("A2").GetString());
        Assert.Equal(2, N("B2"));          // T-1001 + T-1003; the duplicate T-1003 is excluded
        Assert.Equal(19650m, N("C2"));
        Assert.Equal(9970m, N("D2"));      // t1001 and "T 1003" normalise onto D001's trips
        Assert.Equal(2000m, N("E2"));      // "d001" normalises to D001
        Assert.Equal(7680m, N("F2"));
        Assert.Equal(7480m, N("F3"));      // D002: 8,120 − 640 ("D-002")
        Assert.Equal(3900m, N("D103"));    // T-9999 has no trip
        Assert.Equal(1, N("B104"));        // one duplicate trip
        Assert.Equal("OK", s.Cell("F105").GetString());
        Assert.Equal("NO TRIP", wb.Worksheet("Fuel").Cell("E4").GetString());
        Assert.Equal("DUPLICATE", wb.Worksheet("Trips").Cell("G5").GetString());
        Assert.Equal("", wb.Worksheet("Trips").Cell("G4").GetString()); // first copy is kept
    }
}
