using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MimeKit;

namespace SettleFlow.Tests;

/// <summary>End-to-end over HTTP: sign-up confirmation, invites and password reset, including the emails and links.</summary>
public sealed partial class AccountFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "settleflow-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public AccountFlowTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_dir, "test.db")}");
            b.UseSetting("DataProtection:KeysPath", Path.Combine(_dir, "keys"));
            b.UseSetting("Storage:Root", Path.Combine(_dir, "files"));
            b.UseSetting("Email:Mode", "Pickup");
            b.UseSetting("Email:PickupDirectory", Path.Combine(_dir, "mail"));
            b.UseSetting("Ollama:Enabled", "false");
            b.UseSetting("Scanning:Mode", "Off");
            b.UseSetting("Billing:AllowManualPlanChange", "true");
            b.UseSetting("App:PublicUrl", "http://localhost");
        });
    }

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient c, string getUrl, Dictionary<string, string> fields, string? postUrl = null)
    {
        var html = await c.GetStringAsync(getUrl);
        fields["__RequestVerificationToken"] = Token().Match(html).Groups[1].Value;
        return await c.PostAsync(postUrl ?? getUrl, new FormUrlEncodedContent(fields));
    }

    private string LatestEmailLink(string to, string path)
    {
        var file = Directory.GetFiles(Path.Combine(_dir, "mail"), "*.eml").OrderByDescending(File.GetCreationTimeUtc)
            .Select(f => MimeMessage.Load(f)).First(m => m.To.Mailboxes.Any(x => x.Address == to));
        var url = Regex.Match(file.TextBody ?? "", $@"http://localhost{Regex.Escape(path)}\S+").Value;
        Assert.False(string.IsNullOrEmpty(url), $"No {path} link in email to {to}");
        return url.Replace("http://localhost", "");
    }

    [Fact]
    public async Task Invite_accept_and_password_reset()
    {
        const string ownerPw = "OwnerPass1234", memberPw = "MemberPass1234", newPw = "NewOwnerPass5678";
        var owner = NewClient();
        var r = await PostFormAsync(owner, "/Account/Register", new() { ["Input.Organization"] = "Acme", ["Input.Email"] = "owner@acme.test", ["Input.Password"] = ownerPw });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/Account/Login", r.Headers.Location?.OriginalString);

        // The owner cannot sign in until they prove control of the registration address.
        r = await PostFormAsync(owner, "/Account/Login", new() { ["Email"] = "owner@acme.test", ["Password"] = ownerPw });
        Assert.Contains("Confirm your email address", await r.Content.ReadAsStringAsync());
        var confirmation = LatestEmailLink("owner@acme.test", "/Account/ConfirmEmail");
        r = await owner.GetAsync(confirmation);
        Assert.Equal("/Workflows", r.Headers.Location?.OriginalString);

        // Trial has one seat; inviting must be refused until the plan allows more.
        r = await PostFormAsync(owner, "/Settings", new() { ["MemberEmail"] = "member@acme.test" }, "/Settings?handler=AddMember");
        Assert.Contains("seats on your plan are in use", await r.Content.ReadAsStringAsync());

        r = await PostFormAsync(owner, "/Settings", new() { ["Plan"] = "Business", ["RetentionDays"] = "30" }, "/Settings?handler=Org");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        r = await PostFormAsync(owner, "/Settings", new() { ["MemberEmail"] = "member@acme.test" }, "/Settings?handler=AddMember");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Invited · pending", await owner.GetStringAsync("/Settings"));

        // Member accepts via the emailed link.
        var invite = LatestEmailLink("member@acme.test", "/Account/AcceptInvite");
        var member = NewClient();
        Assert.Contains("Join Acme", await member.GetStringAsync(invite));
        r = await PostFormAsync(member, invite, new() { ["Password"] = memberPw });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Acme", await member.GetStringAsync("/"));
        Assert.Contains("Invitation expired", await NewClient().GetStringAsync(invite)); // single use

        // Owner forgets their password.
        var anon = NewClient();
        r = await PostFormAsync(anon, "/Account/ForgotPassword", new() { ["Email"] = "owner@acme.test" });
        Assert.Contains("If an account exists", await r.Content.ReadAsStringAsync());
        r = await PostFormAsync(anon, "/Account/ForgotPassword", new() { ["Email"] = "nobody@acme.test" });
        Assert.Contains("If an account exists", await r.Content.ReadAsStringAsync()); // no account enumeration

        var reset = LatestEmailLink("owner@acme.test", "/Account/ResetPassword");
        r = await PostFormAsync(anon, reset, new() { ["Password"] = newPw });
        Assert.Equal("/Account/Login", r.Headers.Location?.OriginalString);

        var fresh = NewClient();
        r = await PostFormAsync(fresh, "/Account/Login", new() { ["Email"] = "owner@acme.test", ["Password"] = ownerPw });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // old password rejected (page re-rendered)
        r = await PostFormAsync(fresh, "/Account/Login", new() { ["Email"] = "owner@acme.test", ["Password"] = newPw });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
    }

    [Fact]
    public async Task Health_and_unconfigured_webhooks()
    {
        var c = NewClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/readyz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/webhooks/stripe", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/Billing")).StatusCode); // requires sign-in
    }

    [Fact]
    public async Task Confirmation_resend_does_not_reveal_accounts()
    {
        const string password = "OwnerPass1234";
        var c = NewClient();
        await PostFormAsync(c, "/Account/Register", new() { ["Input.Organization"] = "Acme", ["Input.Email"] = "owner@acme.test", ["Input.Password"] = password });

        var known = await PostFormAsync(c, "/Account/ResendConfirmation", new() { ["Email"] = "owner@acme.test" });
        var unknown = await PostFormAsync(c, "/Account/ResendConfirmation", new() { ["Email"] = "nobody@acme.test" });
        const string message = "If an unconfirmed account exists";
        Assert.Contains(message, await known.Content.ReadAsStringAsync());
        Assert.Contains(message, await unknown.Content.ReadAsStringAsync());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
