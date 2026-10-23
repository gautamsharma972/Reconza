using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace SettleFlow.Web.Services;

/// <summary>
/// Encrypts the Data Protection key ring at rest with a master key held only in the environment
/// (DataProtection__MasterKey, 32 random bytes as base64). A database dump alone can't decrypt tenant files.
/// </summary>
public sealed class MasterKeyXmlEncryptor(byte[] masterKey) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(masterKey, 16)) aes.Encrypt(nonce, plaintext, cipher, tag);
        var element = new XElement("encryptedKey",
            new XAttribute("alg", "AES-256-GCM"),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("value", Convert.ToBase64String(cipher)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = services.GetRequiredService<MasterKey>().Bytes;
        var nonce = Convert.FromBase64String(encryptedElement.Element("nonce")!.Value);
        var tag = Convert.FromBase64String(encryptedElement.Element("tag")!.Value);
        var cipher = Convert.FromBase64String(encryptedElement.Element("value")!.Value);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plain));
    }
}

public sealed record MasterKey(byte[] Bytes)
{
    /// <summary>
    /// Accepts 32 random bytes as base64 (e.g. `openssl rand -base64 32`) or any other high-entropy secret of
    /// at least 32 characters (such as a host-generated value), which is stretched to 32 bytes with SHA-256.
    /// </summary>
    public static MasterKey FromBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32) return new MasterKey(bytes);
        }
        catch (FormatException) { }
        if (value.Length < 32)
            throw new InvalidOperationException("DataProtection:MasterKey is too short. Use `openssl rand -base64 32` or a generated secret of at least 32 characters.");
        return new MasterKey(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
