using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Pages.Audit;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public List<AuditEvent> Events { get; private set; } = new();
    public async Task OnGetAsync() => Events = await db.AuditEvents.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(1000).ToListAsync();
}
