using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace SettleFlow.Web.Services;

public sealed class EmailOptions
{
    /// <summary>"Smtp" sends real mail; "Pickup" writes .eml files to <see cref="PickupDirectory"/> (development).</summary>
    public string Mode { get; set; } = "Pickup";
    public string FromAddress { get; set; } = "no-reply@reconza.local";
    public string FromName { get; set; } = "Reconza";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    /// <summary>"StartTls" (587), "SslOnConnect" (465) or "None" (local relay only).</summary>
    public string Security { get; set; } = "StartTls";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string? PickupDirectory { get; set; }

    public IEnumerable<string> ProductionErrors()
    {
        if (!string.Equals(Mode, "Smtp", StringComparison.OrdinalIgnoreCase))
            yield return "Email:Mode must be Smtp.";
        if (string.IsNullOrWhiteSpace(SmtpHost))
            yield return "Email:SmtpHost is required.";
        if (SmtpPort is < 1 or > 65535)
            yield return "Email:SmtpPort must be between 1 and 65535.";
        if (!string.Equals(Security, "StartTls", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Security, "SslOnConnect", StringComparison.OrdinalIgnoreCase))
            yield return "Email:Security must be StartTls or SslOnConnect.";
        if (string.IsNullOrWhiteSpace(FromName))
            yield return "Email:FromName is required.";
        if (!MailboxAddress.TryParse(FromAddress, out var from) || from.Address.IndexOf('@') <= 0 || from.Address.EndsWith('@'))
            yield return "Email:FromAddress must be a valid email address.";
        if (string.IsNullOrWhiteSpace(Username) != string.IsNullOrWhiteSpace(Password))
            yield return "Email:Username and Email:Password must either both be set or both be empty.";
        if (TimeoutSeconds is < 5 or > 120)
            yield return "Email:TimeoutSeconds must be between 5 and 120.";
    }
}

public sealed record EmailMessage(string To, string Subject, string Html, string Text);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class EmailSender(EmailOptions options, IWebHostEnvironment env, ILogger<EmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();

        if (!string.Equals(options.Mode, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            var dir = options.PickupDirectory ?? Path.Combine(env.ContentRootPath, "App_Data", "mail");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");
            await mime.WriteToAsync(path, ct);
            log.LogInformation("Email to {To} written to {Path} (pickup mode)", message.To, path);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SmtpHost))
        {
            // Not configured yet: never log message bodies (they contain reset/invite links).
            log.LogWarning("Email to {To} ('{Subject}') not sent: SMTP is not configured", message.To, message.Subject);
            throw new InvalidOperationException("Email is not configured on this server.");
        }

        using var smtp = new SmtpClient();
        smtp.Timeout = checked(options.TimeoutSeconds * 1000);
        var security = options.Security.ToLowerInvariant() switch
        {
            "sslonconnect" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await smtp.ConnectAsync(options.SmtpHost, options.SmtpPort, security, ct);
        if (!string.IsNullOrEmpty(options.Username))
            await smtp.AuthenticateAsync(options.Username, options.Password ?? "", ct);
        await smtp.SendAsync(mime, ct);
        await smtp.DisconnectAsync(true, ct);
        log.LogInformation("Email '{Subject}' sent to {To}", message.Subject, message.To);
    }
}

/// <summary>Transactional email content. All interpolated values are HTML-encoded.</summary>
public static class EmailTemplates
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Layout(string heading, string bodyHtml, string buttonText, string url, string footer) => $"""
        <!doctype html><html><body style="margin:0;background:#f6f7f9;font-family:-apple-system,Segoe UI,Roboto,sans-serif;color:#1b2330">
        <table width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:32px 16px">
        <table width="520" cellpadding="0" cellspacing="0" style="max-width:520px;background:#fff;border:1px solid #e3e7ee;border-radius:12px">
        <tr><td style="padding:28px 32px">
          <div style="font-weight:700;font-size:17px;margin-bottom:22px"><span style="display:inline-block;width:26px;height:26px;line-height:26px;text-align:center;border-radius:7px;background:#1f5eff;color:#fff">⇄</span> Reconza</div>
          <h1 style="font-size:20px;margin:0 0 12px">{E(heading)}</h1>
          {bodyHtml}
          <p style="margin:24px 0"><a href="{E(url)}" style="background:#1f5eff;color:#fff;padding:11px 18px;border-radius:8px;text-decoration:none;font-weight:600;display:inline-block">{E(buttonText)}</a></p>
          <p style="font-size:12px;color:#6a7586">If the button doesn't work, paste this link into your browser:<br><span style="word-break:break-all">{E(url)}</span></p>
          <p style="font-size:12px;color:#6a7586;margin-top:20px">{E(footer)}</p>
        </td></tr></table></td></tr></table></body></html>
        """;

    public static EmailMessage Invite(string to, string orgName, string invitedBy, string url) => new(to,
        $"{invitedBy} invited you to {orgName} on Reconza",
        Layout($"Join {orgName} on Reconza",
            $"<p style=\"line-height:1.5\">{E(invitedBy)} has invited you to reconcile settlements with the {E(orgName)} team.</p>",
            "Accept invitation", url, "This invitation expires in 3 days. If you weren't expecting it, you can ignore this email."),
        $"{invitedBy} invited you to {orgName} on Reconza.\n\nAccept: {url}\n\nThis invitation expires in 3 days.");

    public static EmailMessage ConfirmEmail(string to, string url) => new(to,
        "Confirm your Reconza email",
        Layout("Confirm your email",
            "<p style=\"line-height:1.5\">Confirm this address to finish creating your Reconza account.</p>",
            "Confirm email", url, "This link expires in 3 hours. If you didn't create this account, you can ignore this email."),
        $"Confirm your Reconza email: {url}\n\nThis link expires in 3 hours. If you didn't create this account, ignore this email.");

    public static EmailMessage PasswordReset(string to, string url) => new(to,
        "Reset your Reconza password",
        Layout("Reset your password",
            "<p style=\"line-height:1.5\">We received a request to reset your password. The link below works once and expires in 3 hours.</p>",
            "Choose a new password", url, "If you didn't ask for this, ignore this email — your password won't change."),
        $"Reset your Reconza password: {url}\n\nIf you didn't ask for this, ignore this email.");

    public static EmailMessage RunFinished(string to, string runLabel, string workflow, bool ok, int exceptions, string url) => new(to,
        ok ? $"Report ready: {runLabel}" : $"Check needs attention: {runLabel}",
        Layout(ok ? "Your checked report is ready" : "A check needs attention",
            ok ? $"<p style=\"line-height:1.5\"><strong>{E(workflow)}</strong> — {E(runLabel)} finished with {exceptions} possible issue(s) to review.</p>"
               : $"<p style=\"line-height:1.5\"><strong>{E(workflow)}</strong> — {E(runLabel)} couldn't be processed.</p>",
            ok ? "Review and download" : "See what went wrong", url, "You're receiving this because you started this check."),
        $"{workflow} — {runLabel}: {(ok ? $"ready with {exceptions} possible issue(s)" : "couldn't be processed")}. {url}");
}
