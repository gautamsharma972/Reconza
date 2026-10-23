using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public interface ITenantContext
{
    Guid? TenantId { get; }
    string UserName { get; }
}

public sealed class TenantContext(IHttpContextAccessor http) : ITenantContext
{
    public const string ClaimType = "tenant_id";
    private Guid? _override;
    private string? _userOverride;

    public Guid? TenantId => _override ?? (Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimType), out var t) ? t : null);
    public string UserName => _userOverride ?? http.HttpContext?.User.Identity?.Name ?? "system";

    /// <summary>Used during sign-up and by background jobs that act on behalf of one tenant.</summary>
    public void Use(Guid tenantId, string? user = null) { _override = tenantId; _userOverride = user; }
}

public sealed class TenantClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        id.AddClaim(new Claim(TenantContext.ClaimType, user.TenantId.ToString()));
        if (user.IsOwner) id.AddClaim(new Claim("owner", "true"));
        return id;
    }
}
