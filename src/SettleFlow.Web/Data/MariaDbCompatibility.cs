using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SettleFlow.Web.Data;

/// <summary>
/// Makes Oracle's MySQL EF provider work on MariaDB. The provider takes its migration lock with
/// <c>GET_LOCK('__EFMigrationsLock', -1)</c>; MySQL treats -1 as "wait forever" but MariaDB returns NULL,
/// which crashes start-up. A positive timeout behaves the same on both servers.
/// </summary>
public sealed partial class MariaDbCompatibilityInterceptor : DbCommandInterceptor
{
    public const int LockTimeoutSeconds = 3600;

    public static string Rewrite(string sql) =>
        NegativeGetLock().Replace(sql, m => $"GET_LOCK({m.Groups[1].Value}, {LockTimeoutSeconds})");

    private static void Fix(DbCommand command) => command.CommandText = Rewrite(command.CommandText);

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Fix(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Fix(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Fix(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Fix(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Fix(command); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Fix(command); return ValueTask.FromResult(result); }

    [GeneratedRegex(@"GET_LOCK\(\s*('[^']*')\s*,\s*-\d+\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex NegativeGetLock();
}
