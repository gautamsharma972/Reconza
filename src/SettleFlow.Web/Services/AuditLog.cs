using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public sealed class AuditLog(AppDbContext db, ITenantContext tenant)
{
    /// <summary>Adds an audit event to the current unit of work; saved with the caller's SaveChanges.</summary>
    public void Record(string action, string details, Guid? entityId = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant."),
            User = tenant.UserName,
            Action = action,
            Details = details.Length > 2000 ? details[..2000] : details,
            EntityId = entityId,
        });
}
