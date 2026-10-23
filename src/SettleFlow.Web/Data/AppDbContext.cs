using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Data;

public class AppDbContext(DbContextOptions options, ITenantContext tenant)
    : IdentityDbContext<AppUser>(options), Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.IDataProtectionKeyContext
{
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys => Set<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunFile> RunFiles => Set<RunFile>();
    public DbSet<RunException> RunExceptions => Set<RunException>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>Current tenant; every tenant-owned query is filtered by it.</summary>
    private Guid? CurrentTenantId => tenant.TenantId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // Tenant isolation: rows from other tenants are invisible to every query.
        // A request without a tenant (anonymous / background job) sees nothing unless it uses IgnoreQueryFilters() explicitly.
        b.Entity<Workflow>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Run>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<RunFile>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<RunException>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<AuditEvent>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        b.Entity<Workflow>().HasIndex(e => new { e.TenantId, e.Name });
        b.Entity<Run>().HasIndex(e => new { e.TenantId, e.CreatedAt });
        b.Entity<Run>().HasIndex(e => new { e.Status, e.CreatedAt });
        b.Entity<Run>().Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<RunException>().Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Tenant>().Property(e => e.Plan).HasConversion<string>().HasMaxLength(20);
        b.Entity<Run>().HasMany(r => r.Files).WithOne().HasForeignKey(f => f.RunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RunException>().HasIndex(e => new { e.TenantId, e.RunId });
        b.Entity<RunException>().HasOne<Run>().WithMany().HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AuditEvent>().HasIndex(e => new { e.TenantId, e.At });
        b.Entity<AppUser>().HasIndex(u => u.TenantId);
        b.Entity<ProcessedWebhookEvent>().HasKey(e => e.Id);
        b.Entity<StoredFile>().HasKey(f => f.Key);
        b.Entity<StoredFile>().Property(f => f.Key).HasMaxLength(200);
        b.Entity<StoredFile>().HasIndex(f => f.TenantId);
        b.Entity<ProcessedWebhookEvent>().Property(e => e.Id).HasMaxLength(200);
        b.Entity<Tenant>().HasIndex(t => t.SubscriptionId);
        b.Entity<Tenant>().HasIndex(t => t.BillingCustomerId);

        if (Database.ProviderName == "MySql.EntityFrameworkCore")
        {
            // MySQL can't index unbounded text (LONGTEXT); give every indexed or key string an explicit length.
            b.Entity<Workflow>().Property(w => w.Name).HasMaxLength(200);
            b.Entity<Tenant>().Property(t => t.SubscriptionId).HasMaxLength(200);
            b.Entity<Tenant>().Property(t => t.BillingCustomerId).HasMaxLength(200);
            b.Entity<AppUser>().Property(u => u.Id).HasMaxLength(255);
            b.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().Property(r => r.Id).HasMaxLength(255);
            b.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>(e =>
            {
                e.Property(l => l.LoginProvider).HasMaxLength(128);
                e.Property(l => l.ProviderKey).HasMaxLength(128);
            });
            b.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>(e =>
            {
                e.Property(t => t.LoginProvider).HasMaxLength(128);
                e.Property(t => t.Name).HasMaxLength(128);
            });
        }

        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            p.SetPrecision(18);
            p.SetScale(4);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>New rows get the current tenant; writes to another tenant's rows are refused.</summary>
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty && CurrentTenantId is { } t)
                entry.Entity.TenantId = t;
            if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                && CurrentTenantId is { } cur && entry.Entity.TenantId != cur)
                throw new InvalidOperationException("Cross-tenant write blocked.");
        }
    }
}

/// <summary>
/// Same model on SQLite (single-server hosting such as Plesk). Exists so SQLite gets its own migration set:
/// EF binds migrations to a context type, and PostgreSQL and SQLite need different SQL.
/// </summary>
public sealed class SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options, ITenantContext tenant)
    : AppDbContext(options, tenant);

/// <summary>Same model on MySQL / MariaDB (e.g. Plesk databases), with its own migration set.</summary>
public sealed class MySqlAppDbContext(DbContextOptions<MySqlAppDbContext> options, ITenantContext tenant)
    : AppDbContext(options, tenant);
