using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SettleFlow.Web.Data;

namespace SettleFlow.Tests;

/// <summary>
/// Full product flow against a real MySQL server. Opt-in: set RECONZA_MYSQL to a connection string for an
/// empty, disposable database (the test drops and recreates it).
/// </summary>
public sealed partial class MySqlEndToEndTests : IDisposable
{
    private readonly string? _cs = Environment.GetEnvironmentVariable("RECONZA_MYSQL");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reconza-mysql-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program>? _factory;

    private WebApplicationFactory<Program> Factory()
    {
        Directory.CreateDirectory(_dir);
        // Start from an empty database so migrations are exercised from scratch.
        using (var db = new MySqlAppDbContext(new DbContextOptionsBuilder<MySqlAppDbContext>().UseMySQL(_cs!).AddInterceptors(new MariaDbCompatibilityInterceptor()).Options, new NullTenant()))
            db.Database.EnsureDeleted();
        return _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Database:Provider", "MySql");
            b.UseSetting("ConnectionStrings:MySql", _cs);
            b.UseSetting("Storage:Provider", "Database");              // encrypted files as LONGBLOB in MySQL
            b.UseSetting("DataProtection:MasterKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i * 7)).ToArray()));
            b.UseSetting("Email:Mode", "Pickup");
            b.UseSetting("Email:PickupDirectory", Path.Combine(_dir, "mail"));
            b.UseSetting("Ollama:Enabled", "false");
            b.UseSetting("Scanning:Mode", "Off");
            b.UseSetting("Billing:AllowManualPlanChange", "true");
            b.UseSetting("App:PublicUrl", "http://localhost");
        });
    }

    private sealed class NullTenant : SettleFlow.Web.Services.ITenantContext { public Guid? TenantId => null; public string UserName => "test"; }

    private static async Task<HttpResponseMessage> Post(HttpClient c, string getUrl, Dictionary<string, string> fields, string? postUrl = null)
    {
        var html = await c.GetStringAsync(getUrl);
        fields["__RequestVerificationToken"] = Token().Match(html).Groups[1].Value;
        return await c.PostAsync(postUrl ?? getUrl, new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Full_product_flow_on_mysql()
    {
        if (string.IsNullOrEmpty(_cs)) return; // opt-in
        var factory = Factory();
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // Sign up (runs all MySQL migrations on first request) and lift the seat limit.
        var r = await Post(c, "/Account/Register", new() { ["Input.Organization"] = "MySQL Transport Co", ["Input.Email"] = "owner@mysql.test", ["Input.Password"] = "OwnerPass1234" });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        await Post(c, "/Settings", new() { ["Plan"] = "Business", ["RetentionDays"] = "30" }, "/Settings?handler=Org");

        // Workflow from the driver-settlement template, then a background run on the demo files.
        r = await Post(c, "/Workflows", new() { ["Name"] = "Weekly pay", ["Preset"] = "driver-settlement" }, "/Workflows?handler=Create");
        var workflowId = Regex.Match(r.Headers.Location!.OriginalString, @"[0-9a-f\-]{36}").Value;
        r = await Post(c, $"/Runs/New/{workflowId}", new(), $"/Runs/New/{workflowId}?handler=Sample");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var runUrl = r.Headers.Location!.OriginalString;

        string status = "";
        for (var i = 0; i < 300 && status is not ("Completed" or "Failed"); i++)
        {
            await Task.Delay(100);
            using var doc = JsonDocument.Parse(await c.GetStringAsync(runUrl + "?handler=Status"));
            status = doc.RootElement.GetProperty("status").GetString()!;
        }
        Assert.Equal("Completed", status);
        var details = await c.GetStringAsync(runUrl);
        Assert.Contains("80,005.00", details);                         // same net as SQLite / unit tests
        var runId = Regex.Match(runUrl, @"[0-9a-f\-]{36}").Value;
        Assert.Contains("3,847.40", await c.GetStringAsync($"/Runs/Statement/{runId}?group=D003"));
        var report = await c.GetAsync(runUrl + "?handler=Download");
        using (var wb = new ClosedXML.Excel.XLWorkbook(await report.Content.ReadAsStreamAsync()))
            Assert.Contains(wb.Worksheets, w => w.Name == "Settlement");

        // Invite + accept, password reset (tokens and Identity tables on MySQL).
        r = await Post(c, "/Settings", new() { ["MemberEmail"] = "member@mysql.test" }, "/Settings?handler=AddMember");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var mail = Directory.GetFiles(Path.Combine(_dir, "mail")).Select(f => MimeMessage.Load(f)).First(m => m.To.Mailboxes.Any(x => x.Address == "member@mysql.test"));
        var invite = Regex.Match(mail.TextBody ?? "", @"http://localhost(/Account/AcceptInvite\S+)").Groups[1].Value;
        var member = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        r = await Post(member, invite, new() { ["Password"] = "MemberPass1234" });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("Weekly pay", await member.GetStringAsync("/Workflows"));  // same tenant, sees the workflow

        // What landed in MySQL.
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MySqlAppDbContext>();
        Assert.Contains("InitialMySql", string.Join(",", await store.Database.GetAppliedMigrationsAsync()));
        Assert.True(await store.StoredFiles.CountAsync() >= 6);              // 4 uploads + report + details, encrypted
        Assert.True(await store.DataProtectionKeys.CountAsync() >= 1);       // key ring in the database
        Assert.DoesNotContain("<masterKey", (await store.DataProtectionKeys.FirstAsync()).Xml);
        Assert.Equal(9, await store.RunExceptions.IgnoreQueryFilters().CountAsync());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    public void Dispose() { _factory?.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
