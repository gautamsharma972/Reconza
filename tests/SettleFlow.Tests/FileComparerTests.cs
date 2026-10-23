using SettleFlow.Core.Engine;
using SettleFlow.Core.Parsing;
using SettleFlow.Core.Presets;

namespace SettleFlow.Tests;

public class FileComparerTests
{
    private static RawTable Load(string name)
    {
        var f = SampleData.DriverSettlement().Single(f => f.FileName == name);
        return TabularFileReader.Read(new MemoryStream(f.Content), TabularFileReader.FormatFromFileName(name));
    }

    [Fact]
    public void Finds_key_columns_and_differences_automatically()
    {
        var trips = Load("trips.xlsx");
        var customer = Load("customer_shipments.xlsx");

        var key = FileComparer.SuggestKeys(trips, customer).First();
        Assert.Equal(("Trip No", "Shipment ID"), (key.ColumnA, key.ColumnB));
        Assert.Equal("Freight Pay", FileComparer.SuggestAmount(trips, key.ColumnA));
        Assert.Equal("Billed Amount", FileComparer.SuggestAmount(customer, key.ColumnB));

        var r = FileComparer.Compare(trips, customer, "Trip No", "Shipment ID", "Freight Pay", "Billed Amount");
        Assert.Equal(["T-1015"], r.OnlyInA.Select(x => x.Key));
        Assert.Empty(r.OnlyInB);
        Assert.Single(r.DuplicatesA, d => d.Key == "T-1007");
        var diff = Assert.Single(r.AmountDiffs);
        Assert.Equal(("T-1004", -1250m), (diff.Key, diff.Difference));
        Assert.Equal(19, r.Matched);
    }

    [Fact]
    public void Keys_match_across_formatting_differences()
    {
        var trips = Load("trips.xlsx");
        var fuel = Load("fuel.csv"); // "t1001" style refs
        var key = FileComparer.SuggestKeys(trips, fuel).First();
        Assert.Equal(("Trip No", "Load Ref"), (key.ColumnA, key.ColumnB));
        var r = FileComparer.Compare(trips, fuel, "Trip No", "Load Ref", null, null);
        Assert.Contains(r.OnlyInB, x => x.Key == "T-9999");
        Assert.True(r.Matched >= 9);
    }
}
