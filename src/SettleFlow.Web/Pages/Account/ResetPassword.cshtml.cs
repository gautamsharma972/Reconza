using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ResetPasswordModel(UserManager<AppUser> users, AppDbContext db, TenantContext tenant, AuditLog audit) : PageModel
{
    [BindProperty(SupportsGet = true)] public string UserId { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";

    public IActionResult OnGet() => string.IsNullOrEmpty(UserId) || string.IsNullOrEmpty(Token) ? RedirectToPage("ForgotPassword") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await users.FindByIdAsync(UserId);
        var result = user is null ? IdentityResult.Failed(new IdentityError { Description = "This link is invalid or has expired." })
                                  : await users.ResetPasswordAsync(user, Token, Password);
        if (!result.Succeeded)
        {
            foreach (var e in result.Errors)
                ModelState.AddModelError("", e.Code == "InvalidToken" ? "This link is invalid or has expired. Request a new one." : e.Description);
            return Page();
        }
        await users.ResetAccessFailedCountAsync(user!);
        await users.SetLockoutEndDateAsync(user!, null);
        tenant.Use(user!.TenantId, user.Email);
        audit.Record("user.password_reset", $"{user.Email} reset their password.");
        await db.SaveChangesAsync();
        TempData["Flash"] = "Password updated. Sign in with your new password.";
        return RedirectToPage("Login");
    }
}
