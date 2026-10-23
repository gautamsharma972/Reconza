using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

/// <summary>
/// Stores encrypted files in PostgreSQL. For small deployments without object storage (e.g. free hosting
/// without a persistent disk). Same per-tenant encryption as the other stores; switch to S3 as volume grows.
/// </summary>
public sealed class DatabaseFileStore(IServiceScopeFactory scopes, IDataProtectionProvider dp) : IFileStore
{
    private IDataProtector Protector(Guid tenantId) => dp.CreateProtector("SettleFlow.Files", tenantId.ToString());

    public async Task<string> SaveAsync(Guid tenantId, string kind, byte[] content, CancellationToken ct = default)
    {
        var key = $"{tenantId:N}/{kind}/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}.bin";
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.StoredFiles.Add(new StoredFile { Key = key, TenantId = tenantId, Data = Protector(tenantId).Protect(content) });
        await db.SaveChangesAsync(ct);
        return key;
    }

    public async Task<byte[]> ReadAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        Guard(tenantId, key);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = await db.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Key == key && f.TenantId == tenantId, ct)
                   ?? throw new FileNotFoundException("Stored file not found.", key);
        return Protector(tenantId).Unprotect(file.Data);
    }

    public async Task DeleteAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        Guard(tenantId, key);
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().StoredFiles
            .Where(f => f.Key == key && f.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    private static void Guard(Guid tenantId, string key)
    {
        if (!key.StartsWith($"{tenantId:N}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Storage key does not belong to this tenant.");
    }
}
