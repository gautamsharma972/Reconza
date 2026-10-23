using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages;

public sealed class IndexModel(AppDbContext db, ITenantContext tenant) : PageModel
{
    public static readonly (string Q, string A)[] Faq =
    {
        ("Do I need to change how my customers, vendors or drivers send data?", "No. Reconza reads the Excel and CSV files you already receive — title rows, merged headers, different date formats and IDs written differently (T-1001 vs t1001) are handled automatically."),
        ("Is AI calculating my settlement?", "No. Matching and every rupee in the settlement come from fixed, tested rules, so the same files always give the same result. AI only suggests which column is which when you set up a new file, and you confirm it."),
        ("What does it catch?", "Duplicate trips or orders, records missing from one file, amounts that don't match between files (e.g. short-paid freight), unreadable values like 'N/A' in an amount column, and fuel fills or deductions that can't be tied to a driver."),
        ("What do I get at the end?", "An Excel settlement workbook (summary, settlement per driver or channel, exceptions, and a cleaned copy of every source), plus a printable statement for each driver, and a full audit trail."),
        ("Is my data safe?", "Each company's data is isolated, files are encrypted at rest, every upload is checked and workbooks with macros or embedded objects are rejected, and uploaded files are deleted automatically after your retention period."),
        ("How much does it cost?", "There's a free trial with no card. Paid plans start at ₹999/month (or $19/month outside India) and are billed monthly — cancel any time."),
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
