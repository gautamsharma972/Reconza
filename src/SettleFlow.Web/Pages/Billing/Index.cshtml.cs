using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Pages.Billing;

public sealed class IndexModel(AppDbContext db, ITenantContext tenant, BillingService billing) : PageModel
{
    public Tenant Org { get; private set; } = null!;
    public bool IsOwner => User.HasClaim("owner", "true");
    public bool StripeOn => billing.Options.Stripe.Configured;
    public bool RazorpayOn => billing.Options.Razorpay.Configured;
    public bool Subscribed => Org.SubscriptionStatus is "active" or "past_due" && Org.SubscriptionId is not null;
    [BindProperty(SupportsGet = true)] public string? Checkout { get; set; }
    public string? Error { get; private set; }

    private async Task LoadAsync() => Org = (await db.Tenants.FindAsync(tenant.TenantId))!;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostSubscribeAsync(Plan plan, string provider, CancellationToken ct)
    {
        await LoadAsync();
        if (!IsOwner) return Forbid();
        if (plan == Plan.Trial) return BadRequest();
        try
        {
            if (provider == "Razorpay" && RazorpayOn)
            {
                if (Subscribed && Org.BillingProvider == "Razorpay")
                {
                    await billing.RazorpayChangePlanAsync(Org, plan, ct);
                    TempData["Flash"] = $"Plan change to {plan} requested. It applies as soon as Razorpay confirms.";
                    return RedirectToPage();
                }
                return Redirect(await billing.RazorpaySubscribeUrlAsync(Org, plan, User.Identity!.Name!, ct));
            }
            if (provider == "Stripe" && StripeOn)
            {
                if (Subscribed && Org.BillingProvider == "Stripe") return Redirect(await billing.StripePortalUrlAsync(Org, ct));
                return Redirect(await billing.StripeCheckoutUrlAsync(Org, plan, User.Identity!.Name!, ct));
            }
            return BadRequest();
        }
        catch (BillingException ex) { Error = ex.Message; return Page(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Error = "Couldn't reach the payment provider. Please try again in a minute.";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostManageAsync(CancellationToken ct)
    {
        await LoadAsync();
        if (!IsOwner || Org.BillingProvider != "Stripe") return Forbid();
        return Redirect(await billing.StripePortalUrlAsync(Org, ct));
    }

    public async Task<IActionResult> OnPostCancelAsync(CancellationToken ct)
    {
        await LoadAsync();
        if (!IsOwner || Org.BillingProvider != "Razorpay" || !Subscribed) return Forbid();
        try { await billing.RazorpayCancelAsync(Org, ct); TempData["Flash"] = "Your subscription will end at the close of the current billing period."; }
        catch (BillingException ex) { TempData["Flash"] = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { TempData["Flash"] = "Couldn't reach the payment provider. Please try again."; }
        return RedirectToPage();
    }
}
