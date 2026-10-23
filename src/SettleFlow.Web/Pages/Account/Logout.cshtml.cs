using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Pages.Account;

public sealed class LogoutModel(SignInManager<AppUser> signIn) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Index");
    }
}
