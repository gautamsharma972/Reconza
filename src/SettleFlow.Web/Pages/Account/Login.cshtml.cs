using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Pages.Account;

[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
public sealed class LoginModel(SignInManager<AppUser> signIn) : PageModel
{
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var result = await signIn.PasswordSignInAsync(Email.Trim(), Password, isPersistent: false, lockoutOnFailure: true);
        if (result.Succeeded)
            return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl) : RedirectToPage("/Index");
        Error = result.IsLockedOut ? "Too many attempts. Try again in 15 minutes." : "Email or password is incorrect.";
        return Page();
    }
}
