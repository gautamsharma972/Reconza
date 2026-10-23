using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ForgotPasswordModel(UserManager<AppUser> users, IEmailSender email, AppUrls urls, ILogger<ForgotPasswordModel> log) : PageModel
{
    [BindProperty] public string Email { get; set; } = "";
    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        // Same response whether or not the account exists, so the form can't be used to discover accounts.
        Sent = true;
        var user = await users.FindByEmailAsync(Email.Trim());
        if (user is null || user.PasswordHash is null || !user.EmailConfirmed) return Page();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        try { await email.SendAsync(EmailTemplates.PasswordReset(user.Email!, urls.ResetPassword(user.Id, token))); }
        catch (Exception ex) { log.LogError(ex, "Password reset email failed"); }
        return Page();
    }
}
