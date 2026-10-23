using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public class TenantIsolationTests
{
    private sealed class FixedTenant(Guid? id) : ITenantContext
    {
        public Guid? TenantId { get; set; } = id;
        public string UserName => "test";
    }

    private static AppDbContext Db(SqliteConnection conn, ITenantContext t) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options, t);

    [Fact]
    public async Task Tenants_cannot_see_or_modify_each_others_data()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        using (var setup = Db(conn, new FixedTenant(null))) setup.Database.EnsureCreated();

        Guid aWorkflow;
        using (var db = Db(conn, new FixedTenant(a)))
        {
            var wf = new Workflow { Name = "A's workflow" };
            db.Workflows.Add(wf);
            await db.SaveChangesAsync();
            aWorkflow = wf.Id;
            Assert.Equal(a, wf.TenantId); // stamped automatically
        }

        using (var db = Db(conn, new FixedTenant(b)))
        {
            Assert.Empty(await db.Workflows.ToListAsync());
            Assert.Null(await db.Workflows.FirstOrDefaultAsync(w => w.Id == aWorkflow));
            db.Workflows.Add(new Workflow { Name = "smuggled", TenantId = a });
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        using (var db = Db(conn, new FixedTenant(null)))
            Assert.Empty(await db.Workflows.ToListAsync()); // anonymous sees nothing
    }
}
