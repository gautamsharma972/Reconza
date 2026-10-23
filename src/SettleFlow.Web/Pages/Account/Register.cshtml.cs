using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Account;

[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
public sealed class RegisterModel(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, TenantContext tenant, AuditLog audit) : PageModel
{
    public sealed class InputModel
    {
        [Required, StringLength(120)] public string Organization { get; set; } = "";
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        // Organization + owner are created atomically. With connection-retry enabled (MySQL, PostgreSQL) a
        // user-initiated transaction must run inside the execution strategy, which may replay it as a unit.
        var strategy = db.Database.CreateExecutionStrategy();
        var user = await strategy.ExecuteAsync<AppUser?>(async () =>
        {
            db.ChangeTracker.Clear(); // a retried attempt starts clean
            await using var tx = await db.Database.BeginTransactionAsync();
            var org = new Tenant { Name = Input.Organization.Trim() };
            db.Tenants.Add(org);
            await db.SaveChangesAsync();

            var owner = new AppUser { UserName = Input.Email.Trim(), Email = Input.Email.Trim(), TenantId = org.Id, IsOwner = true };
            var created = await users.CreateAsync(owner, Input.Password);
            if (!created.Succeeded)
            {
                foreach (var e in created.Errors) ModelState.AddModelError("", e.Description);
                await tx.RollbackAsync();
                return null;
            }
            tenant.Use(org.Id, owner.Email);
            audit.Record("tenant.created", $"Organization '{org.Name}' created by {owner.Email}.");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return owner;
        });
        if (user is null) return Page();

        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Flash"] = "Welcome! Create your first workflow from a template to get started.";
        return RedirectToPage("/Workflows/Index");
    }
}
