using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace SettleFlow.Core.Presets;

public sealed record SampleFile(string SourceName, string FileName, byte[] Content);

/// <summary>
/// Deliberately messy demo files for the driver-settlement preset: title rows above headers,
/// inconsistent IDs ("T-1001" vs "t1001"), mixed date formats, currency strings, duplicates,
/// an orphan fuel fill, a missing customer record and a billing mismatch.
/// </summary>
public static class SampleData
{
    private static readonly (string Id, string Name)[] Drivers =
        [("D001", "Ravi Kumar"), ("D002", "Maria Lopez"), ("D003", "Sanjay Patel"), ("D004", "James O'Neil"), ("D005", "Aisha Khan")];

    public static readonly DateOnly WeekStart = new(2026, 10, 12);

    private static List<(string Trip, string Driver, string Name, DateOnly Date, decimal Pay)> Trips()
    {
        var trips = new List<(string, string, string, DateOnly, decimal)>();
        for (var i = 0; i < 20; i++)
        {
            var d = Drivers[i % Drivers.Length];
            trips.Add(($"T-{1001 + i}", d.Id, d.Name, WeekStart.AddDays(i % 6), 8000m + (i * 731 % 5000)));
        }
        return trips;
    }

    public static IReadOnlyList<SampleFile> DriverSettlement() =>
    [
        new("Trips", "trips.xlsx", TripsXlsx()),
        new("Fuel", "fuel.csv", FuelCsv()),
        new("Deductions", "deductions.xlsx", DeductionsXlsx()),
        new("Customer report", "customer_shipments.xlsx", CustomerXlsx()),
    ];

    private static byte[] TripsXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Trip Register");
        ws.Cell(1, 1).SetValue("ACME Logistics — Trip Register, week of 12 Oct 2026");
        ws.Cell(3, 1).SetValue("Trip No"); ws.Cell(3, 2).SetValue("Trip Date"); ws.Cell(3, 3).SetValue("Driver ID");
        ws.Cell(3, 4).SetValue("Driver Name"); ws.Cell(3, 5).SetValue("Origin"); ws.Cell(3, 6).SetValue("Destination"); ws.Cell(3, 7).SetValue("Freight Pay");
        var r = 4;
        string[] cities = ["Pune", "Mumbai", "Nashik", "Surat", "Indore", "Nagpur"];
        foreach (var t in Trips())
        {
            ws.Cell(r, 1).SetValue(t.Trip);
            ws.Cell(r, 2).SetValue(t.Date.ToDateTime(TimeOnly.MinValue));
            ws.Cell(r, 3).SetValue(t.Trip == "T-1018" ? "" : t.Driver); // missing driver ID
            ws.Cell(r, 4).SetValue(t.Name);
            ws.Cell(r, 5).SetValue(cities[r % cities.Length]); ws.Cell(r, 6).SetValue(cities[(r + 2) % cities.Length]);
            ws.Cell(r, 7).SetValue(t.Pay);
            r++;
        }
        // Duplicate trip entered twice.
        var dup = Trips()[6];
        ws.Cell(r, 1).SetValue(dup.Trip); ws.Cell(r, 2).SetValue(dup.Date.ToDateTime(TimeOnly.MinValue)); ws.Cell(r, 3).SetValue(dup.Driver);
        ws.Cell(r, 4).SetValue(dup.Name); ws.Cell(r, 7).SetValue(dup.Pay);
        return Save(wb);
    }

    private static byte[] FuelCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Card No,Vehicle,Load Ref,Txn Date,Station,Litres,Amount");
        var trips = Trips();
        var line = 0;
        foreach (var t in trips.Where((_, i) => i % 2 == 0))
        {
            var litres = 80 + line * 7 % 60;
            var amt = litres * 96.4m;
            // Fuel vendor writes trip refs without dashes and in lower case, dates as dd/MM/yyyy, amounts as currency text.
            sb.AppendLine(CultureInfo.InvariantCulture, $"4111-XXXX-{1000 + line},MH12AB{1200 + line},{t.Trip.Replace("-", "").ToLowerInvariant()},{t.Date:dd/MM/yyyy},HP Pump {line},{litres},\"₹{amt:#,##0.00}\"");
            line++;
        }
        sb.AppendLine("4111-XXXX-2001,MH12AB1250,t1003,13/10/2026,IOCL Ring Rd,40,\"₹3,856.00\""); // second fill on T-1003
        sb.AppendLine("4111-XXXX-2002,MH12AB1251,T-9999,14/10/2026,BPCL Hwy,55,\"₹5,302.00\"");     // no such trip
        sb.AppendLine("4111-XXXX-2001,MH12AB1250,t1003,13/10/2026,IOCL Ring Rd,40,\"₹3,856.00\""); // exact duplicate
        sb.AppendLine("4111-XXXX-2003,MH12AB1252,T1009,16/10/2026,HP Pump 9,60,N/A");                // unreadable amount
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static byte[] DeductionsXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Deductions");
        ws.Cell(1, 1).SetValue("Driver Code"); ws.Cell(1, 2).SetValue("Date"); ws.Cell(1, 3).SetValue("Deduction Type"); ws.Cell(1, 4).SetValue("Amount");
        (string, string, string, decimal)[] rows =
        [
            ("d001 ", "12-Oct-2026", "Cash advance", 2000m),
            ("D002", "13-Oct-2026", "Toll", 640m),
            ("D002", "14-Oct-2026", "Toll", 410m),
            ("D003", "15-Oct-2026", "Damage recovery", 1500m),
            ("D-004", "16-Oct-2026", "Cash advance", 3000m),
            ("D005", "16-Oct-2026", "Uniform", 350m),
            ("D002", "13-Oct-2026", "Toll", 640m), // duplicate
        ];
        var r = 2;
        foreach (var (id, date, type, amt) in rows)
        {
            ws.Cell(r, 1).SetValue(id); ws.Cell(r, 2).SetValue(date); ws.Cell(r, 3).SetValue(type); ws.Cell(r, 4).SetValue(amt);
            r++;
        }
        return Save(wb);
    }

    private static byte[] CustomerXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Shipments");
        ws.Cell(1, 1).SetValue("Shipment ID"); ws.Cell(1, 2).SetValue("Delivered On"); ws.Cell(1, 3).SetValue("PO Number"); ws.Cell(1, 4).SetValue("Billed Amount");
        var r = 2;
        foreach (var t in Trips())
        {
            if (t.Trip == "T-1015") continue; // customer has no record of this trip
            var billed = t.Trip == "T-1004" ? t.Pay - 1250m : t.Pay; // short-paid
            ws.Cell(r, 1).SetValue(t.Trip);
            ws.Cell(r, 2).SetValue(t.Date.AddDays(1).ToDateTime(TimeOnly.MinValue));
            ws.Cell(r, 3).SetValue($"PO-{77000 + r}");
            ws.Cell(r, 4).SetValue(billed);
            r++;
        }
        return Save(wb);
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
