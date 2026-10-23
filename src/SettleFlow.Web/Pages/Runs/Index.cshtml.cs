using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Pages.Runs;

public sealed class IndexModel(AppDbContext db) : PageModel
{
    public List<Run> Runs { get; private set; } = new();
    public async Task OnGetAsync() => Runs = await db.Runs.OrderByDescending(r => r.CreatedAt).Take(500).ToListAsync();
}
