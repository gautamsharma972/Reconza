using SettleFlow.Core.Model;

namespace SettleFlow.Core.Presets;

public sealed record WorkflowPreset(string Key, string Name, string Description, Func<WorkflowDefinition> Create);

public static class WorkflowPresets
{
    public static readonly IReadOnlyList<WorkflowPreset> All =
    [
        new("driver-settlement", "Weekly driver settlement",
            "Trips from your TMS, fuel card statements, driver deductions and a customer's shipment report. " +
            "Pays each driver gross freight minus fuel and deductions, and checks billed amounts against the customer.",
            DriverSettlement),
        new("marketplace-payout", "Marketplace payout reconciliation",
            "Your order export, the marketplace's settlement report and its fee report. " +
            "Checks every order was paid at the right amount and computes net receipts per marketplace.",
            MarketplacePayout),
        new("blank", "Blank workflow", "Start from scratch and define your own fields and sources.", () => new WorkflowDefinition()),
    ];

    public static WorkflowPreset? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    public static WorkflowDefinition DriverSettlement() => new()
    {
        GroupByField = "DriverId",
        Fields =
        [
            new() { Name = "DriverId", Label = "Driver ID", Type = FieldType.Text, Synonyms = ["driver id", "driver code", "driver no", "driver number", "emp id", "employee id", "driver"] },
            new() { Name = "DriverName", Label = "Driver name", Type = FieldType.Text, Synonyms = ["driver name", "name", "driver full name"] },
            new() { Name = "TripRef", Label = "Trip reference", Type = FieldType.Text, Synonyms = ["trip id", "trip no", "trip ref", "trip number", "load no", "load number", "load id", "shipment id", "shipment no", "lr no", "consignment no", "bol", "pro number", "reference", "ref"] },
            new() { Name = "Date", Label = "Date", Type = FieldType.Date, Synonyms = ["trip date", "date", "ship date", "delivery date", "txn date", "transaction date", "fill date", "deduction date"] },
            new() { Name = "Amount", Label = "Amount", Type = FieldType.Number, Synonyms = ["amount", "freight", "freight amount", "pay", "driver pay", "rate", "total", "value", "net amount", "billed amount", "charge"] },
            new() { Name = "Litres", Label = "Fuel quantity", Type = FieldType.Number, Synonyms = ["litres", "liters", "gallons", "qty", "quantity", "volume"] },
            new() { Name = "Description", Label = "Description", Type = FieldType.Text, Synonyms = ["description", "type", "deduction type", "reason", "narration", "remarks", "category"] },
        ],
        Sources =
        [
            new()
            {
                Name = "Trips", Role = SourceRole.Primary,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["DriverId"] = "Driver ID", ["DriverName"] = "Driver Name", ["TripRef"] = "Trip No", ["Date"] = "Trip Date", ["Amount"] = "Freight Pay" },
                RequiredFields = ["DriverId", "TripRef", "Amount"], KeyFields = ["TripRef"], AmountField = "Amount",
            },
            new()
            {
                Name = "Fuel", Role = SourceRole.Deduction,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["TripRef"] = "Load Ref", ["Date"] = "Txn Date", ["Litres"] = "Litres", ["Amount"] = "Amount" },
                RequiredFields = ["TripRef", "Amount"], KeyFields = ["TripRef", "Date", "Amount"], MatchFields = ["TripRef"],
                AmountField = "Amount", DateFormats = ["dd/MM/yyyy", "dd/MM/yyyy HH:mm"],
            },
            new()
            {
                Name = "Deductions", Role = SourceRole.Deduction,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["DriverId"] = "Driver Code", ["Date"] = "Date", ["Description"] = "Deduction Type", ["Amount"] = "Amount" },
                RequiredFields = ["DriverId", "Amount"], KeyFields = ["DriverId", "Date", "Description"], AmountField = "Amount",
            },
            new()
            {
                Name = "Customer report", Role = SourceRole.ReferenceOnly,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["TripRef"] = "Shipment ID", ["Date"] = "Delivered On", ["Amount"] = "Billed Amount" },
                RequiredFields = ["TripRef"], KeyFields = ["TripRef"], MatchFields = ["TripRef"], ExpectEveryPrimaryRow = true,
                AmountField = "Amount", CompareField = "Amount", PrimaryCompareField = "Amount", CompareTolerance = 1m,
            },
        ],
        Report = new ReportTemplate
        {
            Title = "Weekly Driver Settlement", GroupLabel = "Driver", NetLabel = "Net payable to driver",
            SourceLabels = new(StringComparer.OrdinalIgnoreCase) { ["Trips"] = "Freight earned", ["Fuel"] = "Fuel advances", ["Deductions"] = "Other deductions" },
        },
    };

    public static WorkflowDefinition MarketplacePayout() => new()
    {
        GroupByField = "Channel",
        Fields =
        [
            new() { Name = "OrderId", Label = "Order ID", Type = FieldType.Text, Synonyms = ["order id", "order no", "order number", "order ref", "sub order id", "reference"] },
            new() { Name = "Channel", Label = "Marketplace", Type = FieldType.Text, Synonyms = ["channel", "marketplace", "platform", "store"] },
            new() { Name = "Date", Label = "Date", Type = FieldType.Date, Synonyms = ["order date", "date", "settlement date", "payout date"] },
            new() { Name = "Amount", Label = "Amount", Type = FieldType.Number, Synonyms = ["amount", "order value", "total", "net amount", "settlement amount", "payout", "fee", "commission"] },
            new() { Name = "FeeType", Label = "Fee type", Type = FieldType.Text, Synonyms = ["fee type", "type", "charge type", "description"] },
        ],
        Sources =
        [
            new()
            {
                Name = "Orders", Role = SourceRole.Primary,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["OrderId"] = "Order ID", ["Channel"] = "Channel", ["Date"] = "Order Date", ["Amount"] = "Order Value" },
                RequiredFields = ["OrderId", "Channel", "Amount"], KeyFields = ["OrderId"], AmountField = "Amount",
            },
            new()
            {
                Name = "Payouts", Role = SourceRole.ReferenceOnly,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["OrderId"] = "Order Number", ["Date"] = "Settlement Date", ["Amount"] = "Gross Amount" },
                RequiredFields = ["OrderId"], KeyFields = ["OrderId"], MatchFields = ["OrderId"], ExpectEveryPrimaryRow = true,
                AmountField = "Amount", CompareField = "Amount", PrimaryCompareField = "Amount", CompareTolerance = 0.5m,
            },
            new()
            {
                Name = "Fees", Role = SourceRole.Deduction,
                ColumnMap = new(StringComparer.OrdinalIgnoreCase) { ["OrderId"] = "Order Number", ["FeeType"] = "Fee Type", ["Amount"] = "Fee Amount" },
                RequiredFields = ["OrderId", "Amount"], KeyFields = ["OrderId", "FeeType"], MatchFields = ["OrderId"], AmountField = "Amount",
            },
        ],
        Report = new ReportTemplate
        {
            Title = "Marketplace Payout Reconciliation", GroupLabel = "Marketplace", NetLabel = "Net receipts",
            SourceLabels = new(StringComparer.OrdinalIgnoreCase) { ["Orders"] = "Gross sales", ["Fees"] = "Marketplace fees" },
        },
    };
}
