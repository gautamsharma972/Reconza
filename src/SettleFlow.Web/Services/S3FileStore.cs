using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.DataProtection;

namespace SettleFlow.Web.Services;

public sealed class S3Options
{
    /// <summary>S3-compatible endpoint (Railway buckets, Cloudflare R2, MinIO). Leave empty for AWS S3.</summary>
    public string? ServiceUrl { get; set; }
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ForcePathStyle { get; set; } = true;
}

/// <summary>
/// Private object storage on any S3-compatible service. Objects are encrypted with the tenant's Data Protection
/// key before upload, so the storage provider only ever holds ciphertext.
/// </summary>
public sealed class S3FileStore : IFileStore, IDisposable
{
    private readonly IAmazonS3 _s3;
    private readonly S3Options _options;
    private readonly IDataProtectionProvider _dp;

    public S3FileStore(S3Options options, IDataProtectionProvider dp)
    {
        _options = options;
        _dp = dp;
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (!string.IsNullOrEmpty(options.ServiceUrl)) { config.ServiceURL = options.ServiceUrl; config.AuthenticationRegion = options.Region; }
        else config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        _s3 = string.IsNullOrEmpty(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    private IDataProtector Protector(Guid tenantId) => _dp.CreateProtector("SettleFlow.Files", tenantId.ToString());

    public async Task<string> SaveAsync(Guid tenantId, string kind, byte[] content, CancellationToken ct = default)
    {
        var key = $"{tenantId:N}/{kind}/{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}.bin";
        using var body = new MemoryStream(Protector(tenantId).Protect(content));
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket, Key = key, InputStream = body, ContentType = "application/octet-stream",
        }, ct);
        return key;
    }

    public async Task<byte[]> ReadAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        Guard(tenantId, key);
        using var res = await _s3.GetObjectAsync(_options.Bucket, key, ct);
        using var ms = new MemoryStream();
        await res.ResponseStream.CopyToAsync(ms, ct);
        return Protector(tenantId).Unprotect(ms.ToArray());
    }

    public async Task DeleteAsync(Guid tenantId, string key, CancellationToken ct = default)
    {
        Guard(tenantId, key);
        await _s3.DeleteObjectAsync(_options.Bucket, key, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try { await _s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _options.Bucket, MaxKeys = 1 }, ct); return true; }
        catch (AmazonS3Exception) { return false; }
    }

    private static void Guard(Guid tenantId, string key)
    {
        if (!key.StartsWith($"{tenantId:N}/", StringComparison.Ordinal) || key.Contains(".."))
            throw new UnauthorizedAccessException("Storage key does not belong to this tenant.");
    }

    public void Dispose() => _s3.Dispose();
}
