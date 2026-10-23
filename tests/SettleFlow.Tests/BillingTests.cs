using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public class BillingTests
{
    private const string StripeSecret = "whsec_test_123";
    private const string RazorSecret = "rzp_webhook_test";

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static BillingOptions Options() => new()
    {
        Stripe = new() { SecretKey = "sk_test_x", WebhookSecret = StripeSecret, Prices = new(StringComparer.OrdinalIgnoreCase) { ["Starter"] = "price_s", ["Business"] = "price_b", ["Team"] = "price_t" } },
        Razorpay = new() { KeyId = "rzp_test_x", KeySecret = "secret", WebhookSecret = RazorSecret, Plans = new(StringComparer.OrdinalIgnoreCase) { ["Starter"] = "plan_s", ["Business"] = "plan_b", ["Team"] = "plan_t" } },
    };

    private static (BillingService Billing, AppDbContext Db, Guid TenantId) Setup(SqliteConnection conn)
    {
        var tenantCtx = new TenantContext(new HttpContextAccessor());
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options, tenantCtx);
        db.Database.EnsureCreated();
        var tenant = new Tenant { Name = "Acme" };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        var billing = new BillingService(new HttpClient(), Options(), new AppUrls(new AppOptions()), db, tenantCtx,
            new AuditLog(db, tenantCtx), NullLogger<BillingService>.Instance, new FixedClock(Now));
        return (billing, db, tenant.Id);
    }

    private static string StripeSign(string payload, long ts, string secret = StripeSecret) =>
        $"t={ts},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{payload}")))}";

    private static string RazorSign(string payload, string secret = RazorSecret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [Fact]
    public void Stripe_signature_rules()
    {
        var ts = Now.ToUnixTimeSeconds();
        const string body = """{"id":"evt_1"}""";
        Assert.True(BillingService.VerifyStripeSignature(body, StripeSign(body, ts), StripeSecret, Now));
        Assert.False(BillingService.VerifyStripeSignature(body + " ", StripeSign(body, ts), StripeSecret, Now));          // tampered
        Assert.False(BillingService.VerifyStripeSignature(body, StripeSign(body, ts, "whsec_other"), StripeSecret, Now));  // wrong secret
        Assert.False(BillingService.VerifyStripeSignature(body, StripeSign(body, ts - 600), StripeSecret, Now));           // replayed (too old)
        Assert.False(BillingService.VerifyStripeSignature(body, "t=abc,v1=zz", StripeSecret, Now));
        Assert.False(BillingService.VerifyStripeSignature(body, null, StripeSecret, Now));
    }

    [Fact]
    public async Task Stripe_subscription_lifecycle_updates_plan()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var (billing, db, tenantId) = Setup(conn);
        var ts = Now.ToUnixTimeSeconds();

        async Task<WebhookResult> Send(string json) => await billing.HandleStripeWebhookAsync(json, StripeSign(json, ts), default);

        var checkout = """{"id":"evt_c","type":"checkout.session.completed","data":{"object":{"client_reference_id":"@@tenantId@@","customer":"cus_1","subscription":"sub_1"}}}""".Replace("@@tenantId@@", tenantId.ToString());
        Assert.Equal(200, (await Send(checkout)).StatusCode);

        var active = """{"id":"evt_u1","type":"customer.subscription.updated","data":{"object":{"id":"sub_1","customer":"cus_1","status":"active","cancel_at_period_end":false,"metadata":{"tenant_id":"@@tenantId@@"},"items":{"data":[{"price":{"id":"price_b"},"current_period_end":1793000000}]}}}}""".Replace("@@tenantId@@", tenantId.ToString());
        Assert.Equal(200, (await Send(active)).StatusCode);
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        Assert.Equal(Plan.Business, t.Plan);
        Assert.Equal("active", t.SubscriptionStatus);
        Assert.Equal("cus_1", t.BillingCustomerId);
        Assert.NotNull(t.CurrentPeriodEnd);

        Assert.Equal("Duplicate ignored", (await Send(active)).Message);

        var pastDue = active.Replace("evt_u1", "evt_u2").Replace("\"active\"", "\"past_due\"");
        await Send(pastDue);
        t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        Assert.Equal(Plan.Business, t.Plan); // grace period keeps the plan
        Assert.Equal("past_due", t.SubscriptionStatus);

        var deleted = active.Replace("evt_u1", "evt_d").Replace("customer.subscription.updated", "customer.subscription.deleted").Replace("\"active\"", "\"canceled\"");
        await Send(deleted);
        t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        Assert.Equal(Plan.Trial, t.Plan);
        Assert.Equal("canceled", t.SubscriptionStatus);

        Assert.Contains(await db.AuditEvents.IgnoreQueryFilters().ToListAsync(), e => e.Action == "billing.subscription_changed" && e.TenantId == tenantId);
    }

    [Fact]
    public async Task Forged_webhooks_change_nothing()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var (billing, db, tenantId) = Setup(conn);
        var json = """{"id":"evt_x","type":"customer.subscription.updated","data":{"object":{"id":"sub_x","status":"active","metadata":{"tenant_id":"@@tenantId@@"},"items":{"data":[{"price":{"id":"price_t"}}]}}}}""".Replace("@@tenantId@@", tenantId.ToString());
        Assert.Equal(400, (await billing.HandleStripeWebhookAsync(json, StripeSign(json, Now.ToUnixTimeSeconds(), "whsec_attacker"), default)).StatusCode);

        var rz = """{"event":"subscription.activated","payload":{"subscription":{"entity":{"id":"sub_r","plan_id":"plan_t","status":"active","notes":{"tenant_id":"@@tenantId@@"}}}}}""".Replace("@@tenantId@@", tenantId.ToString());
        Assert.Equal(400, (await billing.HandleRazorpayWebhookAsync(rz, RazorSign(rz, "wrong"), "e1", default)).StatusCode);

        Assert.Equal(Plan.Trial, (await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId)).Plan);
    }

    [Fact]
    public async Task Razorpay_subscription_lifecycle_updates_plan()
    {
        using var conn = new SqliteConnection("DataSource=:memory:"); conn.Open();
        var (billing, db, tenantId) = Setup(conn);
        string Event(string type, string status, string plan = "plan_s") =>
            """{"event":"@@type@@","created_at":1792999999,"payload":{"subscription":{"entity":{"id":"sub_r1","plan_id":"@@plan@@","customer_id":"cust_1","status":"@@status@@","current_end":1795600000,"notes":{"tenant_id":"@@tenantId@@"}}}}}""".Replace("@@type@@", type.ToString()).Replace("@@plan@@", plan.ToString()).Replace("@@status@@", status.ToString()).Replace("@@tenantId@@", tenantId.ToString());
        async Task Send(string json, string id) => Assert.Equal(200, (await billing.HandleRazorpayWebhookAsync(json, RazorSign(json), id, default)).StatusCode);

        await Send(Event("subscription.activated", "active"), "e1");
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        Assert.Equal((Plan.Starter, "active", "Razorpay"), (t.Plan, t.SubscriptionStatus, t.BillingProvider));

        await Send(Event("subscription.updated", "active", "plan_t"), "e2"); // upgraded
        Assert.Equal(Plan.Team, (await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId)).Plan);

        await Send(Event("subscription.halted", "halted", "plan_t"), "e3");
        t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        Assert.Equal((Plan.Team, "past_due"), (t.Plan, t.SubscriptionStatus));

        await Send(Event("subscription.cancelled", "cancelled", "plan_t"), "e4");
        Assert.Equal(Plan.Trial, (await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId)).Plan);
    }
}
