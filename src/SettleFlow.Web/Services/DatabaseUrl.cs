namespace SettleFlow.Web.Services;

public static class DatabaseUrl
{
    /// <summary>Converts a postgres://user:pass@host:port/db URL (Railway, Heroku, Render) to an Npgsql connection string.</summary>
    public static string ToNpgsql(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["sslmode"] is { } ssl && Enum.TryParse<Npgsql.SslMode>(ssl, true, out var mode)) builder.SslMode = mode;
        return builder.ConnectionString;
    }
}
