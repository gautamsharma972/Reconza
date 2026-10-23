using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages;

public sealed class IndexModel(AppDbContext db, ITenantContext tenant) : PageModel
{
    public static readonly (string Q, string A)[] Faq =
    {
        ("Do I need to reformat my spreadsheets first?", "Usually, no. Reconza reads Excel and CSV files with title rows, different column names, common date formats and IDs written in different ways. You confirm how the columns line up the first time, then reuse that setup."),
        ("Does AI decide whether my numbers are correct?", "No. Matching and calculations use fixed, tested rules, so the same files and settings give the same result. AI can suggest which columns correspond when you set up a new file, but you confirm every suggestion."),
        ("What problems can it find?", "It finds records missing from either file, duplicates, amounts that do not agree, unreadable values and entries that cannot be connected to the right order, invoice, trip, driver or account."),
        ("What do I get at the end?", "You get an Excel report containing a summary, the possible problems, and the source rows behind the result. Driver-payment setups can also create a printable statement for each driver."),
        ("Is my data safe?", "Each company's data is isolated, files are encrypted at rest, every upload is checked and workbooks with macros or embedded objects are rejected, and uploaded files are deleted automatically after your retention period."),
        ("How much does it cost?", "The free plan has no card and no time limit. Paid plans start at ₹999/month (or $19/month outside India), are billed monthly and can be cancelled at any time."),
    };

    public bool SignedIn { get; private set; }
    public Tenant? Org { get; private set; }
    public List<Run> RecentRuns { get; private set; } = new();
    public List<Workflow> Workflows { get; private set; } = new();
    public int OpenExceptions { get; private set; }
    public int RowsThisMonth { get; private set; }
    public int RunsThisMonth { get; private set; }
    public bool HasRuns { get; private set; }
    public bool HasReviewed { get; private set; }
    public bool HasDownloaded { get; private set; }

    public async Task OnGetAsync()
    {
        SignedIn = tenant.TenantId is not null;
        if (!SignedIn) return;
        Org = await db.Tenants.FindAsync(tenant.TenantId);
        RecentRuns = await db.Runs.OrderByDescending(r => r.CreatedAt).Take(8).ToListAsync();
        Workflows = await db.Workflows.OrderBy(w => w.Name).ToListAsync();
        OpenExceptions = await db.RunExceptions.CountAsync(e => e.Status == ExceptionStatus.Open);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var month = await db.Runs.Where(r => r.CreatedAt >= monthStart && r.Status != RunStatus.Failed).Select(r => r.RowsProcessed).ToListAsync();
        HasRuns = await db.Runs.AnyAsync(r => r.Status == RunStatus.Completed);
        HasReviewed = await db.RunExceptions.AnyAsync(e => e.Status != ExceptionStatus.Open);
        HasDownloaded = await db.AuditEvents.AnyAsync(e => e.Action == "report.downloaded");
        RowsThisMonth = month.Sum();
        RunsThisMonth = month.Count;
    }
}
