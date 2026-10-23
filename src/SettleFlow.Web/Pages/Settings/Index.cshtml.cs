using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Settings;

public sealed class IndexModel(AppDbContext db, ITenantContext tenant, UserManager<AppUser> users, AuditLog audit,
    SettleFlow.Core.Mapping.HybridMappingSuggester ai, IMalwareScanner scanner,
    IEmailSender email, AppUrls urls, BillingOptions billing, ILogger<IndexModel> log) : PageModel
{
    public Tenant Org { get; private set; } = null!;
    public List<AppUser> Members { get; private set; } = new();
    public bool IsOwner => User.HasClaim("owner", "true");
    public string AiStatus { get; private set; } = "";
    public string ScanStatus { get; private set; } = "";
    public ScanMode ScanMode => scanner.Mode;
    public string QueueStatus { get; private set; } = "";
    public bool AiOk { get; private set; }
    public bool ScanOk { get; private set; }

    [BindProperty] public Plan Plan { get; set; }
    [BindProperty, Range(1, 3650)] public int RetentionDays { get; set; }
    [BindProperty, EmailAddress] public string? MemberEmail { get; set; }
    /// <summary>Plan can be changed here only in development; in production it follows the billing provider.</summary>
    public bool ManualPlanChange => billing.AllowManualPlanChange;

    private async Task LoadAsync()
    {
        Org = (await db.Tenants.FindAsync(tenant.TenantId))!;
        Members = await users.Users.Where(u => u.TenantId == Org.Id).OrderBy(u => u.Email).ToListAsync();
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();
        Plan = Org.Plan;
        RetentionDays = Org.RetentionDays;
        await LoadStatusAsync();
    }

    private async Task LoadStatusAsync()
    {
        var aiTask = ai.HealthAsync();
        var scanTask = scanner.HealthAsync();
        var queued = await db.Runs.CountAsync(r => r.Status == RunStatus.Queued);
        var processing = await db.Runs.CountAsync(r => r.Status == RunStatus.Processing);
        QueueStatus = processing + queued == 0 ? "Idle — ready for new runs" : $"{processing} processing · {queued} waiting";
        // Customer-facing wording only: infrastructure details (hosts, ports, model names) stay in server logs.
        var aiHealth = await aiTask;
        AiStatus = aiHealth.StartsWith("Ready") ? "Available" : aiHealth == "Off" ? "Not enabled — suggestions use matching rules" : "Temporarily unavailable — suggestions use matching rules";
        AiOk = aiHealth.StartsWith("Ready");
        var scanHealth = await scanTask;
        ScanOk = scanHealth.StartsWith("Ready");
        ScanStatus = ScanOk ? "Active" : scanHealth == "Off" ? "Built-in file checks" : scanner.Mode == ScanMode.Required
            ? "Temporarily unavailable — uploads paused" : "Temporarily unavailable — built-in file checks only";
    }

    public async Task<IActionResult> OnPostOrgAsync()
    {
        await LoadAsync();
        if (!IsOwner) return Forbid();
        if (!ModelState.IsValid) { await LoadStatusAsync(); return Page(); }
        if (Members.Count > Plans.For(Plan).Seats)
        {
            ModelState.AddModelError("", $"The {Plans.For(Plan).Name} plan has {Plans.For(Plan).Seats} seats but you have {Members.Count} members.");
            await LoadStatusAsync();
            return Page();
        }
        if (!ManualPlanChange) Plan = Org.Plan;
        var changes = new List<string>();
        if (Org.Plan != Plan) changes.Add($"plan {Org.Plan} → {Plan} (manual)");
        if (Org.RetentionDays != RetentionDays) changes.Add($"retention {Org.RetentionDays} → {RetentionDays} days");
        Org.Plan = Plan;
        Org.RetentionDays = RetentionDays;
        if (changes.Count > 0) audit.Record("tenant.settings", string.Join("; ", changes));
        await db.SaveChangesAsync();
        TempData["Flash"] = "Settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAddMemberAsync()
    {
        await LoadAsync();
        if (!IsOwner) return Forbid();
        if (Members.Count >= Plans.For(Org.Plan).Seats)
            ModelState.AddModelError("", $"All {Plans.For(Org.Plan).Seats} seats on your plan are in use. Upgrade to add more people.");
        if (string.IsNullOrWhiteSpace(MemberEmail)) ModelState.AddModelError("", "Enter an email address.");
        if (!ModelState.IsValid) { Plan = Org.Plan; RetentionDays = Org.RetentionDays; await LoadStatusAsync(); return Page(); }

        var address = MemberEmail!.Trim();
        var user = new AppUser { UserName = address, Email = address, TenantId = Org.Id };
        var result = await users.CreateAsync(user); // no password until they accept
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", e.Code is "DuplicateUserName" or "DuplicateEmail" ? "That email already has a Reconza account." : e.Description);
            Plan = Org.Plan; RetentionDays = Org.RetentionDays; await LoadStatusAsync();
            return Page();
        }
        await SendInviteAsync(user);
        audit.Record("member.invited", $"{user.Email} invited.");
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Invitation sent to {user.Email}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResendInviteAsync(string userId)
    {
        await LoadAsync();
        if (!IsOwner) return Forbid();
        var user = Members.FirstOrDefault(u => u.Id == userId && u.PasswordHash is null);
        if (user is null) return BadRequest();
        await users.UpdateSecurityStampAsync(user); // old links stop working
        await SendInviteAsync(user);
        audit.Record("member.invite_resent", $"Invitation re-sent to {user.Email}.");
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Invitation re-sent to {user.Email}.";
        return RedirectToPage();
    }

    private async Task SendInviteAsync(AppUser user)
    {
        var token = await users.GenerateUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose);
        try { await email.SendAsync(EmailTemplates.Invite(user.Email!, Org.Name, tenant.UserName, urls.AcceptInvite(user.Id, token))); }
        catch (Exception ex) { log.LogError(ex, "Invite email to {Email} failed", user.Email); TempData["Flash"] = "The invitation couldn't be emailed — check SMTP settings and resend."; }
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(string userId)
    {
        await LoadAsync();
        if (!IsOwner) return Forbid();
        var user = Members.FirstOrDefault(u => u.Id == userId);
        if (user is null || user.IsOwner) return BadRequest();
        await users.DeleteAsync(user);
        audit.Record("member.removed", $"{user.Email} removed.");
        await db.SaveChangesAsync();
        TempData["Flash"] = $"Removed {user.Email}.";
        return RedirectToPage();
    }
}
