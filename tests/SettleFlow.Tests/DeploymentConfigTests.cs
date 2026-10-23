using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public class DeploymentConfigTests
{
    [Fact]
    public void Neon_style_database_url_is_parsed()
    {
        var cs = new NpgsqlConnectionStringBuilder(DatabaseUrl.ToNpgsql(
            "postgresql://app_user:p%40ss%2Fword@ep-cool-name-123456.ap-southeast-1.aws.neon.tech/neondb?sslmode=require&channel_binding=require"));
        Assert.Equal("ep-cool-name-123456.ap-southeast-1.aws.neon.tech", cs.Host);
        Assert.Equal(5432, cs.Port);
        Assert.Equal("app_user", cs.Username);
        Assert.Equal("p@ss/word", cs.Password);
        Assert.Equal("neondb", cs.Database);
        Assert.Equal(SslMode.Require, cs.SslMode);
    }

    [Fact]
    public void Master_key_accepts_base64_or_long_secrets()
    {
        var raw = new byte[32]; Random.Shared.NextBytes(raw);
        Assert.Equal(raw, MasterKey.FromBase64(Convert.ToBase64String(raw)).Bytes);
        Assert.Equal(32, MasterKey.FromBase64("a-host-generated-secret-value-that-is-long-enough").Bytes.Length);
        Assert.Throws<InvalidOperationException>(() => MasterKey.FromBase64("short"));
    }

    [Fact]
    public void Key_ring_round_trips_through_master_key_encryption()
    {
        var key = MasterKey.FromBase64(Convert.ToBase64String(new byte[32].Select((_, i) => (byte)i).ToArray()));
        var element = new System.Xml.Linq.XElement("key", new System.Xml.Linq.XElement("secret", "PLAINTEXT-SECRET-42"));
        var encrypted = new MasterKeyXmlEncryptor(key.Bytes).Encrypt(element);
        Assert.DoesNotContain("PLAINTEXT-SECRET-42", encrypted.EncryptedElement.ToString());
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(key).BuildServiceProvider();
        Assert.Equal(element.ToString(), new MasterKeyXmlDecryptor(services).Decrypt(encrypted.EncryptedElement).ToString());
    }
}

public class CompanySettingsTests
{
    [Fact]
    public void Example_placeholders_never_pass_the_production_check()
    {
        var example = new CompanyOptions
        {
            LegalName = "Your Company Private Limited", Address = "Street, City, State PIN, India",
            Email = "support@your-domain.com", Jurisdiction = "City, State", EffectiveDate = "2026-11-01",
        };
        Assert.Equal(4, example.Missing().Count());

        var real = new CompanyOptions
        {
            LegalName = "Reconza Technologies Pvt Ltd", Address = "12 Todorov Lane, Pune 411001, India",
            Email = "support@reconza.com", Jurisdiction = "Pune, Maharashtra", EffectiveDate = "2026-11-01",
        };
        Assert.Empty(real.Missing());
        Assert.Contains("Company:EffectiveDate", WithDate("not a date").Missing());
    }

    private static CompanyOptions WithDate(string d) => new() { LegalName = "A Ltd", Address = "B", Email = "c@d.in", Jurisdiction = "Pune", EffectiveDate = d };
}

public class EmailSettingsTests
{
    [Fact]
    public void Secure_complete_smtp_settings_pass_production_validation()
    {
        var options = new EmailOptions
        {
            Mode = "Smtp", SmtpHost = "smtp.example.com", SmtpPort = 465, Security = "SslOnConnect",
            Username = "mailer@example.com", Password = "secret", FromAddress = "mailer@example.com", FromName = "Reconza",
        };

        Assert.Empty(options.ProductionErrors());
    }

    [Fact]
    public void Insecure_or_incomplete_smtp_settings_fail_production_validation()
    {
        var options = new EmailOptions
        {
            Mode = "Pickup", SmtpHost = "", SmtpPort = 0, Security = "None", Username = "mailer@example.com",
            Password = "", FromAddress = "not-an-email", FromName = "", TimeoutSeconds = 1,
        };

        var errors = options.ProductionErrors().ToList();
        Assert.True(errors.Count >= 7);
        Assert.Contains(errors, e => e.Contains("Email:Mode"));
        Assert.Contains(errors, e => e.Contains("SmtpHost"));
        Assert.Contains(errors, e => e.Contains("StartTls or SslOnConnect"));
        Assert.Contains(errors, e => e.Contains("Username and Email:Password"));
    }

    [Fact]
    public void Transactional_templates_encode_user_controlled_html()
    {
        var message = EmailTemplates.Invite("member@example.com", "<script>team</script>", "<owner>", "https://example.com/a?x=1&y=2");

        Assert.DoesNotContain("<script>team</script>", message.Html);
        Assert.Contains("&lt;script&gt;team&lt;/script&gt;", message.Html);
        Assert.Contains("https://example.com/a?x=1&amp;y=2", message.Html);
        Assert.Contains("https://example.com/a?x=1&y=2", message.Text);
    }
}
