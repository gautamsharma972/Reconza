using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Tests;

public class MySqlModelTests
{
    /// <summary>MySQL rejects keys and indexes on TEXT/BLOB columns; catch that at build time, not at deploy time.</summary>
    [Fact]
    public void Every_key_and_index_column_has_an_indexable_type()
    {
        using var db = new MySqlDesignTimeFactory().CreateDbContext([]);
        var problems = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var indexed = entity.GetKeys().SelectMany(k => k.Properties)
                .Concat(entity.GetIndexes().SelectMany(i => i.Properties))
                .Concat(entity.GetForeignKeys().SelectMany(f => f.Properties))
                .Distinct();
            foreach (var p in indexed)
            {
                var type = p.GetColumnType();
                if (type.Contains("text", StringComparison.OrdinalIgnoreCase) || type.Contains("blob", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{entity.GetTableName()}.{p.GetColumnName()} is {type}");
            }
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void MySql_migrations_match_the_model()
    {
        using var db = new MySqlDesignTimeFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

public class MariaDbCompatibilityTests
{
    [Theory]
    [InlineData("SELECT GET_LOCK('__EFMigrationsLock',-1);", "SELECT GET_LOCK('__EFMigrationsLock', 3600);")]
    [InlineData("select get_lock( '__EFMigrationsLock' , -1 )", "select GET_LOCK('__EFMigrationsLock', 3600)")]
    [InlineData("SELECT GET_LOCK('x', 10);", "SELECT GET_LOCK('x', 10);")]             // positive timeouts untouched
    [InlineData("SELECT RELEASE_LOCK('__EFMigrationsLock');", "SELECT RELEASE_LOCK('__EFMigrationsLock');")]
    [InlineData("SELECT * FROM Runs WHERE Progress = -1", "SELECT * FROM Runs WHERE Progress = -1")]
    public void Only_negative_get_lock_timeouts_are_rewritten(string input, string expected) =>
        Assert.Equal(expected, MariaDbCompatibilityInterceptor.Rewrite(input));
}
