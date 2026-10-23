using ClosedXML.Excel;

namespace SettleFlow.Core.Reporting;

/// <summary>
/// Free downloadable "weekly driver settlement" workbook: paste trips, fuel and deductions, get net pay per driver.
/// Uses only functions available in Excel 2010+ and Google Sheets (SUMIFS, COUNTIFS, IFERROR, INDEX/MATCH).
/// </summary>
public static class SettlementTemplate
{
    private const int Rows = 500; // formula range per input sheet

    public static byte[] DriverSettlement()
    {
        using var wb = new XLWorkbook();
        var head = XLColor.FromHtml("#1F5EFF");
        var input = XLColor.FromHtml("#F4F7FF");

        void Header(IXLWorksheet ws, params string[] names)
        {
            for (var i = 0; i < names.Length; i++) ws.Cell(1, i + 1).Value = names[i];
            ws.Range(1, 1, 1, names.Length).Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(head);
            ws.SheetView.FreezeRows(1);
        }

        // ---- Instructions
        var guide = wb.AddWorksheet("Start here");
        string[] lines =
        [
            "Weekly driver settlement template",
            "",
            "1. Paste your trip register into 'Trips' (columns A–D). Keep the header row.",
            "2. Paste fuel card fills into 'Fuel' (columns A–C). Use the trip number the driver entered at the pump.",
            "3. Paste advances, tolls and other deductions into 'Deductions' (columns A–C).",
            "4. List your driver codes in 'Settlement' column A. Net payable is calculated for each driver.",
            "",
            "Built-in checks:",
            " • Trips: 'Duplicate?' flags the second and later copies of a trip number. Flagged rows are left out of pay.",
            " • Fuel: 'Driver (from trip)' finds the driver through the trip; 'NO TRIP' means the trip number didn't match.",
            " • Trip and driver codes are compared after removing spaces and dashes and ignoring case (T-1001 = t1001).",
            " • 'Check' on the Settlement sheet should read OK: totals per driver must add up to the sheet totals.",
            "",
            "Doing this every week? Reconza does all of this automatically from the files you already receive,",
            "explains every exception and gives each driver an itemized statement: https://www.reconza.com",
        ];
        for (var i = 0; i < lines.Length; i++) guide.Cell(i + 1, 1).Value = lines[i];
        guide.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16);
        guide.Cell(8, 1).Style.Font.SetBold();
        guide.Column(1).Width = 110;

        // Normalised key: upper case, no spaces or dashes.
        static string Key(string cell) => $"UPPER(SUBSTITUTE(SUBSTITUTE(TRIM({cell}),\"-\",\"\"),\" \",\"\"))";

        // ---- Trips
        var trips = wb.AddWorksheet("Trips");
        Header(trips, "Trip No", "Date", "Driver Code", "Freight Pay", "Trip key", "Driver key", "Duplicate?");
        (string, int, string, decimal)[] sampleTrips = [("T-1001", 0, "D001", 9450), ("T-1002", 1, "D002", 8120), ("T-1003", 1, "D001", 10200), ("T-1003", 1, "D001", 10200)];
        for (var r = 0; r < sampleTrips.Length; r++)
        {
            var (no, day, driver, pay) = sampleTrips[r];
            trips.Cell(r + 2, 1).Value = no;
            trips.Cell(r + 2, 2).Value = new DateTime(2026, 10, 12).AddDays(day);
            trips.Cell(r + 2, 3).Value = driver;
            trips.Cell(r + 2, 4).Value = pay;
        }
        for (var r = 2; r <= Rows + 1; r++)
        {
            trips.Cell(r, 5).FormulaA1 = $"IF(A{r}=\"\",\"\",{Key($"A{r}")})";
            trips.Cell(r, 6).FormulaA1 = $"IF(C{r}=\"\",\"\",{Key($"C{r}")})";
            // Running count: the first copy of a trip stays blank, later copies are flagged and excluded from totals.
            trips.Cell(r, 7).FormulaA1 = $"IF(E{r}=\"\",\"\",IF(COUNTIF($E$2:E{r},E{r})>1,\"DUPLICATE\",\"\"))";
        }
        trips.Range(2, 1, Rows + 1, 4).Style.Fill.SetBackgroundColor(input);
        trips.Column(2).Style.DateFormat.Format = "dd-mmm-yyyy";
        trips.Column(4).Style.NumberFormat.Format = "#,##0.00";
        trips.Columns(5, 6).Style.Font.SetFontColor(XLColor.Gray);

        // ---- Fuel
        var fuel = wb.AddWorksheet("Fuel");
        Header(fuel, "Trip No", "Date", "Amount", "Trip key", "Driver (from trip)");
        (string, int, decimal)[] sampleFuel = [("t1001", 0, 4820), ("T 1003", 1, 5150), ("T-9999", 2, 3900)];
        for (var r = 0; r < sampleFuel.Length; r++)
        {
            fuel.Cell(r + 2, 1).Value = sampleFuel[r].Item1;
            fuel.Cell(r + 2, 2).Value = new DateTime(2026, 10, 12).AddDays(sampleFuel[r].Item2);
            fuel.Cell(r + 2, 3).Value = sampleFuel[r].Item3;
        }
        for (var r = 2; r <= Rows + 1; r++)
        {
            fuel.Cell(r, 4).FormulaA1 = $"IF(A{r}=\"\",\"\",{Key($"A{r}")})";
            fuel.Cell(r, 5).FormulaA1 = $"IF(D{r}=\"\",\"\",IFERROR(INDEX(Trips!$F$2:$F${Rows + 1},MATCH(D{r},Trips!$E$2:$E${Rows + 1},0)),\"NO TRIP\"))";
        }
        fuel.Range(2, 1, Rows + 1, 3).Style.Fill.SetBackgroundColor(input);
        fuel.Column(2).Style.DateFormat.Format = "dd-mmm-yyyy";
        fuel.Column(3).Style.NumberFormat.Format = "#,##0.00";
        fuel.Column(4).Style.Font.SetFontColor(XLColor.Gray);

        // ---- Deductions
        var ded = wb.AddWorksheet("Deductions");
        Header(ded, "Driver Code", "Type", "Amount", "Driver key");
        (string, string, decimal)[] sampleDed = [("d001", "Cash advance", 2000), ("D-002", "Toll", 640)];
        for (var r = 0; r < sampleDed.Length; r++)
        {
            ded.Cell(r + 2, 1).Value = sampleDed[r].Item1;
            ded.Cell(r + 2, 2).Value = sampleDed[r].Item2;
            ded.Cell(r + 2, 3).Value = sampleDed[r].Item3;
        }
        for (var r = 2; r <= Rows + 1; r++)
            ded.Cell(r, 4).FormulaA1 = $"IF(A{r}=\"\",\"\",{Key($"A{r}")})";
        ded.Range(2, 1, Rows + 1, 3).Style.Fill.SetBackgroundColor(input);
        ded.Column(3).Style.NumberFormat.Format = "#,##0.00";
        ded.Column(4).Style.Font.SetFontColor(XLColor.Gray);

        // ---- Settlement
        var set = wb.AddWorksheet("Settlement");
        Header(set, "Driver Code", "Trips", "Freight earned", "Fuel advances", "Other deductions", "Net payable", "Driver key");
        string[] drivers = ["D001", "D002"];
        const int setRows = 100;
        for (var i = 0; i < drivers.Length; i++) set.Cell(i + 2, 1).Value = drivers[i];
        var t = Rows + 1;
        for (var r = 2; r <= setRows + 1; r++)
        {
            set.Cell(r, 7).FormulaA1 = $"IF(A{r}=\"\",\"\",{Key($"A{r}")})";
            // Rows flagged DUPLICATE are excluded; the first copy of each trip counts.
            set.Cell(r, 2).FormulaA1 = $"IF(G{r}=\"\",\"\",COUNTIFS(Trips!$F$2:$F${t},G{r},Trips!$G$2:$G${t},\"\"))";
            set.Cell(r, 3).FormulaA1 = $"IF(G{r}=\"\",\"\",SUMIFS(Trips!$D$2:$D${t},Trips!$F$2:$F${t},G{r},Trips!$G$2:$G${t},\"\"))";
            set.Cell(r, 4).FormulaA1 = $"IF(G{r}=\"\",\"\",SUMIFS(Fuel!$C$2:$C${t},Fuel!$E$2:$E${t},G{r}))";
            set.Cell(r, 5).FormulaA1 = $"IF(G{r}=\"\",\"\",SUMIFS(Deductions!$C$2:$C${t},Deductions!$D$2:$D${t},G{r}))";
            set.Cell(r, 6).FormulaA1 = $"IF(G{r}=\"\",\"\",C{r}-D{r}-E{r})";
        }
        set.Range(2, 1, setRows + 1, 1).Style.Fill.SetBackgroundColor(input);
        set.Range(2, 3, setRows + 1, 6).Style.NumberFormat.Format = "#,##0.00";
        set.Column(6).Style.Font.SetBold();
        set.Column(7).Style.Font.SetFontColor(XLColor.Gray);

        var summary = setRows + 3;
        set.Cell(summary, 1).Value = "Unmatched fuel (NO TRIP)";
        set.Cell(summary, 4).FormulaA1 = $"SUMIFS(Fuel!$C$2:$C${t},Fuel!$E$2:$E${t},\"NO TRIP\")";
        set.Cell(summary + 1, 1).Value = "Duplicate trips found";
        set.Cell(summary + 1, 2).FormulaA1 = $"COUNTIF(Trips!$G$2:$G${t},\"DUPLICATE\")";
        set.Cell(summary + 2, 1).Value = "Check";
        set.Cell(summary + 2, 6).FormulaA1 =
            $"IF(ABS(SUM(D2:D{setRows + 1})+D{summary}-SUM(Fuel!$C$2:$C${t}))+ABS(SUM(E2:E{setRows + 1})-SUM(Deductions!$C$2:$C${t}))<0.01,\"OK\",\"Totals don't match — a driver code is missing from column A\")";
        set.Range(summary, 1, summary + 2, 1).Style.Font.SetBold();
        set.Range(summary, 4, summary + 2, 6).Style.NumberFormat.Format = "#,##0.00";

        foreach (var ws in new[] { trips, fuel, ded, set }) ws.Columns(1, 7).Width = 16;
        set.Column(1).Width = 26;
        set.SetTabActive();
        guide.SetTabActive();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
