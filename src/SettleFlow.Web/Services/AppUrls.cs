using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public sealed class AppOptions
{
    /// <summary>Public base URL used in emails and payment redirects, e.g. https://app.settleflow.in. Never taken from the request Host header.</summary>
    public string PublicUrl { get; set; } = "";
    /// <summary>Public contact address shown on the site (footer, security page). Hidden when empty.</summary>
    public string? SupportEmail { get; set; }
}

/// <summary>The business behind the service, shown on legal and contact pages. Required in production.</summary>
public sealed class CompanyOptions
{
    public string LegalName { get; set; } = "";
    public string Address { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    /// <summary>City and state whose courts have jurisdiction, e.g. "Pune, Maharashtra".</summary>
    public string Jurisdiction { get; set; } = "";
    public string? Gstin { get; set; }
    /// <summary>Date the current legal terms took effect, e.g. "2026-11-01".</summary>
    public string EffectiveDate { get; set; } = "";

    /// <summary>Settings that are empty or still contain the example placeholders.</summary>
    public IEnumerable<string> Missing()
    {
        if (Unset(LegalName)) yield return "Company:LegalName";
        if (Unset(Address)) yield return "Company:Address";
        if (Unset(Email)) yield return "Company:Email";
        if (Unset(Jurisdiction)) yield return "Company:Jurisdiction";
        if (Unset(EffectiveDate) || !DateOnly.TryParse(EffectiveDate, out _)) yield return "Company:EffectiveDate";
    }

    private static readonly string[] Placeholders = ["your company", "street, city", "city, state", "your-domain", "example.com", "change_me"];

    public static bool Unset(string? value) =>
        string.IsNullOrWhiteSpace(value) || Placeholders.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase));
}

public sealed class AppUrls(AppOptions options)
{
    private string Base => options.PublicUrl.TrimEnd('/');
    public string Absolute(string pathAndQuery) => Base + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
    public string Run(Guid runId) => Absolute($"/Runs/Details/{runId}");
    public string ResetPassword(string userId, string token) => Absolute($"/Account/ResetPassword?userId={Uri.EscapeDataString(userId)}&token={Uri.EscapeDataString(token)}");
    public string AcceptInvite(string userId, string token) => Absolute($"/Account/AcceptInvite?userId={Uri.EscapeDataString(userId)}&token={Uri.EscapeDataString(token)}");
}

/// <summary>Invitation links live longer (3 days) than password-reset links (3 hours).</summary>
public sealed class InviteTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public InviteTokenProviderOptions() { Name = InviteTokenProvider.ProviderName; TokenLifespan = TimeSpan.FromDays(3); }
}

public sealed class InviteTokenProvider(IDataProtectionProvider dp, IOptions<InviteTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<AppUser>> log)
    : DataProtectorTokenProvider<AppUser>(dp, options, log)
{
    public const string ProviderName = "Invite";
    public const string Purpose = "accept-invite";
}
