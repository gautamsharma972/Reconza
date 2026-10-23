using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace SettleFlow.Web.Services;

/// <summary>Private, tenant-scoped object storage. Swap for an S3 implementation in production.</summary>
public interface IFileStore
{
    Task<string> SaveAsync(Guid tenantId, string kind, byte[] content, CancellationToken ct = default);
    Task<byte[]> ReadAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task DeleteAsync(Guid tenantId, string key, CancellationToken ct = default);
}

/// <summary>
/// Local-disk store that encrypts every object at rest with a per-tenant Data Protection key
/// (authenticated encryption), so a leaked file or a cross-tenant key lookup yields nothing readable.
/// </summary>
public sealed partial class EncryptedLocalFileStore(IDataProtectionProvider dp, IConfiguration config, IWebHostEnvironment env) : IFileStore
{
    private readonly string _root = Path.GetFullPath(config["Storage:Root"] ?? Path.Combine(env.ContentRootPath, "App_Data", "files"));

    private IDataProtector Protector(Guid tenantId) => dp.CreateProtector("SettleFlow.Files", tenantId.ToString());

    public async Task<string> SaveAsync(Guid tenantId, string kind, byte[] content, CancellationToken ct = default)
    {
        if (!SafeSegment().IsMatch(kind)) throw new ArgumentException("Invalid storage kind.", nameof(kind));
        var key = $"{tenantId:N}/{kind}/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}.bin";
        var path = PathFor(tenantId, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Protector(tenantId).Protect(content), ct);
        return key;
    }

    public async Task<byte[]> ReadAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        Protector(tenantId).Unprotect(await File.ReadAllBytesAsync(PathFor(tenantId, key), ct));

    public Task DeleteAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        var path = PathFor(tenantId, key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(Guid tenantId, string key)
    {
        if (!key.StartsWith($"{tenantId:N}/", StringComparison.Ordinal) || key.Contains(".."))
            throw new UnauthorizedAccessException("Storage key does not belong to this tenant.");
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Invalid storage key.");
        return full;
    }

    [GeneratedRegex("^[a-z0-9-]{1,32}$")] private static partial Regex SafeSegment();
}
