using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SettleFlow.Web.Services;

namespace SettleFlow.Web.Data;

/// <summary>Used by `dotnet ef` to generate PostgreSQL migrations. No database connection is needed to generate them.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private sealed class NoTenant : ITenantContext { public Guid? TenantId => null; public string UserName => "design"; }

    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=settleflow_design").Options, new NoTenant());
}

/// <summary>Used by `dotnet ef ... --context SqliteAppDbContext` to generate SQLite migrations.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    private sealed class NoTenant : ITenantContext { public Guid? TenantId => null; public string UserName => "design"; }

    public SqliteAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite("Data Source=design.db").Options, new NoTenant());
}

/// <summary>Used by `dotnet ef ... --context MySqlAppDbContext` to generate MySQL migrations (no server needed).</summary>
public sealed class MySqlDesignTimeFactory : IDesignTimeDbContextFactory<MySqlAppDbContext>
{
    private sealed class NoTenant : ITenantContext { public Guid? TenantId => null; public string UserName => "design"; }

    public MySqlAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MySqlAppDbContext>().UseMySQL("Server=localhost;Database=design;User=design;Password=design").Options, new NoTenant());
}
