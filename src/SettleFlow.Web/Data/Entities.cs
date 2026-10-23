using Microsoft.AspNetCore.Identity;

namespace SettleFlow.Web.Data;

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public enum Plan { Trial, Starter, Business, Team }

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Plan Plan { get; set; } = Plan.Trial;
    public int RetentionDays { get; set; } = 30;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Subscription state, written only by verified payment-provider webhooks.
    /// <summary>"Stripe" or "Razorpay" once the tenant has subscribed.</summary>
    public string? BillingProvider { get; set; }
    public string? BillingCustomerId { get; set; }
    public string? SubscriptionId { get; set; }
    /// <summary>Provider status normalized to: active, past_due, canceled, pending.</summary>
    public string? SubscriptionStatus { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
}

/// <summary>Encrypted file contents for the database storage provider.</summary>
public sealed class StoredFile
{
    public string Key { get; set; } = "";
    public Guid TenantId { get; set; }
    public byte[] Data { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Webhook event ids already applied, so provider retries are idempotent.</summary>
public sealed class ProcessedWebhookEvent
{
    public string Id { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AppUser : IdentityUser
{
    public Guid TenantId { get; set; }
    public bool IsOwner { get; set; }
}

public sealed class Workflow : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? PresetKey { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum RunStatus { Queued, Processing, Completed, Failed }

public sealed class Run : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkflowId { get; set; }
    public string WorkflowName { get; set; } = "";
    public int Number { get; set; }
    public string Label { get; set; } = "";
    public RunStatus Status { get; set; }
    public string? Error { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long DurationMs { get; set; }
    /// <summary>Human-readable step while processing, e.g. "Reading Fuel".</summary>
    public string? Stage { get; set; }
    public int Progress { get; set; }
    public int Attempts { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>Updated by the worker while it holds the run; stale heartbeats are re-queued.</summary>
    public DateTime? HeartbeatAt { get; set; }
    /// <summary>Normalized rows per source (compressed JSON) for per-group statements.</summary>
    public string? DetailsStorageKey { get; set; }
    public int RowsProcessed { get; set; }
    public int GroupCount { get; set; }
    public int ExceptionCount { get; set; }
    public decimal NetTotal { get; set; }
    /// <summary>Exact workflow definition used, so old runs stay reproducible after edits.</summary>
    public string DefinitionSnapshotJson { get; set; } = "{}";
    /// <summary>Serialized settlement lines and per-source stats.</summary>
    public string SummaryJson { get; set; } = "{}";
    public string? ReportStorageKey { get; set; }
    public DateTime? FilesPurgedAt { get; set; }
    public List<RunFile> Files { get; set; } = new();
}

public sealed class RunFile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public string SourceName { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public int Rows { get; set; }
    public string? StorageKey { get; set; }
    /// <summary>Clean, NotScanned or Infected (infected files are never stored).</summary>
    public string ScanStatus { get; set; } = "NotScanned";
    public string? ScanDetail { get; set; }
}

public enum ExceptionStatus { Open, Accepted, Resolved }

public sealed class RunException : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RunId { get; set; }
    public string Source { get; set; } = "";
    public int? RowNumber { get; set; }
    public string Type { get; set; } = "";
    public string Key { get; set; } = "";
    public string Message { get; set; } = "";
    public decimal? Amount { get; set; }
    public bool ExcludedFromTotals { get; set; }
    public ExceptionStatus Status { get; set; }
    public string? Note { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public sealed class AuditEvent : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public Guid? EntityId { get; set; }
}
