using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[EnableRateLimiting("auth")]
public sealed class ResendConfirmationModel(UserManager<AppUser> users, IEmailSender email,
    AppUrls urls, ILogger<ResendConfirmationModel> log) : PageModel
{
    [BindProperty] public string Email { get; set; } = "";
    public bool Sent { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        Sent = true;
        var user = await users.FindByEmailAsync(Email.Trim());
        // The response is deliberately identical for unknown, invited and already-confirmed addresses.
        if (user is null || user.EmailConfirmed || user.PasswordHash is null) return Page();

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        try { await email.SendAsync(EmailTemplates.ConfirmEmail(user.Email!, urls.ConfirmEmail(user.Id, token))); }
        catch (Exception ex) { log.LogError(ex, "Confirmation email resend failed for user {UserId}", user.Id); }
        return Page();
    }
}
