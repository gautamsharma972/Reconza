using ClosedXML.Excel;
using SettleFlow.Core.Engine;

namespace SettleFlow.Core.Reporting;

public static class ComparisonReportWriter
{
    public static void Write(CompareResult r, string nameA, string nameB, Stream output)
    {
        using var wb = new XLWorkbook();
        var s = wb.AddWorksheet("Summary");
        var rows = new (string, XLCellValue)[]
        {
            ("File A", ExcelReportWriter.SafeText(nameA)), ("File B", ExcelReportWriter.SafeText(nameB)),
            ("Matched on", ExcelReportWriter.SafeText($"{r.KeyA} ↔ {r.KeyB}")),
            ("Rows in A", r.RowsA), ("Rows in B", r.RowsB), ("Matched keys", r.Matched),
            ("Only in A", r.OnlyInA.Count), ("Only in B", r.OnlyInB.Count),
            ("Duplicates in A", r.DuplicatesA.Count), ("Duplicates in B", r.DuplicatesB.Count),
            ("Amount differences", r.AmountDiffs.Count),
        };
        for (var i = 0; i < rows.Length; i++) { s.Cell(i + 1, 1).SetValue(rows[i].Item1).Style.Font.SetBold(); s.Cell(i + 1, 2).SetValue(rows[i].Item2); }
        s.Cell(rows.Length + 2, 1).SetValue("Generated free by Reconza — automate this every week at reconza.com");
        s.Columns().AdjustToContents(1, 80);

        void List(string title, IEnumerable<CompareRow> items)
        {
            var ws = wb.AddWorksheet(title);
            ws.Cell(1, 1).SetValue("Row"); ws.Cell(1, 2).SetValue("Key"); ws.Cell(1, 3).SetValue("Amount");
            ws.Range(1, 1, 1, 3).Style.Font.SetBold();
            var n = 2;
            foreach (var x in items)
            {
                ws.Cell(n, 1).SetValue(x.RowNumber);
                ws.Cell(n, 2).SetValue(ExcelReportWriter.SafeText(x.Key));
                if (x.Amount is { } a) ws.Cell(n, 3).SetValue(a);
                for (var c = 0; c < x.Cells.Count; c++) ws.Cell(n, 4 + c).SetValue(ExcelReportWriter.SafeText(x.Cells[c]));
                n++;
            }
            ws.Columns().AdjustToContents(1, 50);
        }
        List("Only in A", r.OnlyInA);
        List("Only in B", r.OnlyInB);
        List("Duplicates in A", r.DuplicatesA);
        List("Duplicates in B", r.DuplicatesB);

        var d = wb.AddWorksheet("Amount differences");
        string[] h = ["Key", "Row in A", "Row in B", "Amount A", "Amount B", "Difference"];
        for (var i = 0; i < h.Length; i++) d.Cell(1, i + 1).SetValue(h[i]);
        d.Range(1, 1, 1, h.Length).Style.Font.SetBold();
        var row = 2;
        foreach (var x in r.AmountDiffs)
        {
            d.Cell(row, 1).SetValue(ExcelReportWriter.SafeText(x.Key)); d.Cell(row, 2).SetValue(x.RowA); d.Cell(row, 3).SetValue(x.RowB);
            d.Cell(row, 4).SetValue(x.AmountA); d.Cell(row, 5).SetValue(x.AmountB); d.Cell(row, 6).SetValue(x.Difference);
            row++;
        }
        d.Columns().AdjustToContents(1, 50);
        wb.SaveAs(output);
    }
}
