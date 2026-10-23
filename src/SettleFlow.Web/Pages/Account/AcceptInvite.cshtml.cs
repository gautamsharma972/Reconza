using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class AcceptInviteModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, TenantContext tenant, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string UserId { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public bool Invalid { get; private set; }
    public string OrgName { get; private set; } = "";
    public string Email { get; private set; } = "";

    private async Task<AppUser?> ValidateAsync()
    {
        var user = await users.FindByIdAsync(UserId);
        if (user is null || user.PasswordHash is not null
            || !await users.VerifyUserTokenAsync(user, InviteTokenProvider.ProviderName, InviteTokenProvider.Purpose, Token))
        { Invalid = true; return null; }
        Email = user.Email!;
        OrgName = (await db.Tenants.FindAsync(user.TenantId))?.Name ?? "your team";
        return user;
    }

    public async Task<IActionResult> OnGetAsync() { await ValidateAsync(); return Page(); }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await ValidateAsync();
        if (user is null) return Page();
        var result = await users.AddPasswordAsync(user, Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors) ModelState.AddModelError("", e.Description);
            return Page();
        }
        user.EmailConfirmed = true; // they proved control of the address by using the link
        await users.UpdateSecurityStampAsync(user); // invalidates the invite link
        await users.UpdateAsync(user);
        tenant.Use(user.TenantId, user.Email);
        audit.Record("member.joined", $"{user.Email} accepted the invitation.");
        await db.SaveChangesAsync();
        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Flash"] = $"Welcome to {OrgName}!";
        return RedirectToPage("/Index");
    }
}
