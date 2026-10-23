using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ConfirmEmailModel(UserManager<AppUser> users, SignInManager<AppUser> signIn,
    AppDbContext db, TenantContext tenant, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string UserId { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = "";
    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Token))
            return RedirectToPage("ResendConfirmation");

        var user = await users.FindByIdAsync(UserId);
        if (user is null) return Page();
        if (!user.EmailConfirmed)
        {
            var result = await users.ConfirmEmailAsync(user, Token);
            if (!result.Succeeded) return Page();
            tenant.Use(user.TenantId, user.Email);
            audit.Record("user.email_confirmed", $"{user.Email} confirmed their email address.");
            await db.SaveChangesAsync();
        }

        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Flash"] = "Email confirmed. Welcome to Reconza!";
        return RedirectToPage("/Workflows/Index");
    }
}
