namespace SettleFlow.Web.Services;

/// <summary>Search-engine verification and privacy-friendly analytics, all optional.</summary>
public sealed class SeoOptions
{
    /// <summary>Google Search Console "HTML tag" verification code (the content value only).</summary>
    public string? GoogleVerification { get; set; }
    public string? BingVerification { get; set; }
    /// <summary>Cloudflare Web Analytics token: cookieless, no consent banner needed.</summary>
    public string? CloudflareAnalyticsToken { get; set; }
}

public sealed record SeoPage(string Path, string Title, string Description, DateOnly Updated, string ChangeFrequency = "monthly", double Priority = 0.6);

/// <summary>Public, indexable pages. The sitemap is generated from this list; everything else is noindex.</summary>
public static class Seo
{
    public const string SiteName = "Reconza";
    public const string DefaultDescription =
        "Reconcile trip sheets, fuel card statements and marketplace payouts from your Excel files. Catch duplicates and mismatches, then download the settlement.";

    private static readonly DateOnly Launch = new(2026, 10, 21);

    public static readonly IReadOnlyList<SeoPage> Pages =
    [
        new("/", "Reconza — Driver Settlement & Reconciliation Software", DefaultDescription, Launch, "weekly", 1.0),
        new("/pricing", "Pricing — plans from ₹999/month", "Simple monthly plans for reconciliation and driver settlement. Start free, pay in ₹ via UPI and cards or in $ by card. No setup fee.", Launch, "monthly", 0.8),
        new("/solutions/driver-settlement-software", "Driver settlement software for transporters & fleets", "Automate weekly driver settlements: match trips, fuel card fills and deductions, catch duplicate trips and short-paid freight, and issue driver statements.", Launch, "monthly", 0.9),
        new("/solutions/marketplace-payout-reconciliation", "Marketplace payout reconciliation for online sellers", "Match your order export against marketplace settlement and fee reports. Find unpaid orders, short payments and unexpected fees in minutes, not days.", Launch, "monthly", 0.8),
        new("/solutions/excel-reconciliation-software", "Excel reconciliation software for supplier reports", "Reconcile spreadsheets from customers, vendors and branches without formulas. Messy headers, mixed date formats and inconsistent IDs handled automatically.", Launch, "monthly", 0.8),
        new("/tools/compare-excel-files", "Compare two Excel files online — free", "Free tool: upload two Excel or CSV files and instantly see rows missing from either file, duplicates and amount differences. No sign-up, nothing stored.", Launch, "monthly", 0.9),
        new("/guides", "Reconciliation guides for operations teams", "Practical, step-by-step guides to driver settlements, fuel card reconciliation and marketplace payout matching — with and without Reconza.", Launch, "weekly", 0.6),
        new("/guides/weekly-driver-settlement-excel", "Weekly driver settlement in Excel: a step-by-step guide", "Calculate driver pay from trip sheets, fuel advances and deductions in Excel, step by step — plus the five errors behind most settlement disputes.", Launch),
        new("/guides/driver-settlement-excel-template", "Free driver settlement Excel template (download)", "Free Excel template for weekly driver pay: paste trips, fuel and deductions, get net payable per driver with duplicate and missing-trip checks.", Launch, "monthly", 0.8),
        new("/guides/fuel-card-reconciliation", "How to reconcile fuel card statements with trip sheets", "Match fuel card transactions to trips and drivers, spot fills with no trip, duplicate charges and fuel theft red flags — a practical guide for fleet operators.", Launch),
        new("/guides/marketplace-settlement-reconciliation", "How to reconcile marketplace settlements with orders", "Check that every order was paid at the right amount: matching order exports to Amazon, Flipkart and Shopify-style settlement and fee reports.", Launch),
        new("/privacy", "Privacy policy", "How Reconza collects, uses, stores and deletes personal data and the business files you upload, and the choices and rights you have.", Launch, "yearly", 0.3),
        new("/terms", "Terms of service", "The terms that govern use of Reconza: accounts, acceptable use, your data, fees and billing, availability, liability and termination.", Launch, "yearly", 0.3),
        new("/refund-policy", "Refund & cancellation policy", "How to cancel a Reconza subscription, what happens to your plan and data, and when refunds are given for monthly plans.", Launch, "yearly", 0.3),
        new("/contact", "Contact us", "Contact Reconza for sales, support, billing or security questions. Business name, address and email for our customers.", Launch, "yearly", 0.4),
        new("/security", "Security & data handling", "How Reconza protects your files: per-company isolation, encryption at rest, virus scanning, formula-injection protection, audit logs and automatic deletion.", Launch, "yearly", 0.4),
    ];

    public const string TemplatePath = "/templates/driver-settlement-template.xlsx";

    public static SeoPage? For(string path) => Pages.FirstOrDefault(p => p.Path.Equals(path.TrimEnd('/') is "" ? "/" : path.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
}
