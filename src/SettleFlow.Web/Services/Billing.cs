using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    public string? WebhookSecret { get; set; }
    /// <summary>Plan name → Stripe recurring Price id (USD), e.g. "Starter": "price_123".</summary>
    public Dictionary<string, string> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Configured => !string.IsNullOrEmpty(SecretKey) && !string.IsNullOrEmpty(WebhookSecret) && Prices.Count > 0;
}

public sealed class RazorpayOptions
{
    public string? KeyId { get; set; }
    public string? KeySecret { get; set; }
    public string? WebhookSecret { get; set; }
    /// <summary>Plan name → Razorpay plan id (INR), e.g. "Starter": "plan_123".</summary>
    public Dictionary<string, string> Plans { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Configured => !string.IsNullOrEmpty(KeyId) && !string.IsNullOrEmpty(KeySecret) && !string.IsNullOrEmpty(WebhookSecret) && Plans.Count > 0;
}

public sealed class BillingOptions
{
    /// <summary>Lets owners switch plans without paying. Development only.</summary>
    public bool AllowManualPlanChange { get; set; }
    public StripeOptions Stripe { get; set; } = new();
    public RazorpayOptions Razorpay { get; set; } = new();
}

public sealed record WebhookResult(int StatusCode, string Message);

/// <summary>A billing problem whose message is written for the customer.</summary>
public sealed class BillingException(string message) : Exception(message);

/// <summary>
/// Subscriptions via hosted checkout pages (Stripe Checkout for USD, Razorpay subscription links for INR).
/// The tenant's plan changes only when a signature-verified webhook says so — never on a browser redirect.
/// </summary>
public sealed class BillingService(
    HttpClient http, BillingOptions options, AppUrls urls, AppDbContext db, TenantContext tenantContext,
    AuditLog audit, ILogger<BillingService> log, TimeProvider clock)
{
    private static readonly Plan[] PaidPlans = [Plan.Starter, Plan.Business, Plan.Team];
    public BillingOptions Options => options;

    // ------------------------------------------------------------ Stripe

    public async Task<string> StripeCheckoutUrlAsync(Tenant tenant, Plan plan, string email, CancellationToken ct = default)
    {
        var price = options.Stripe.Prices.GetValueOrDefault(plan.ToString()) ?? throw new BillingException($"No Stripe price configured for {plan}.");
        var form = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["line_items[0][price]"] = price,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = urls.Absolute("/Billing?checkout=success"),
            ["cancel_url"] = urls.Absolute("/Billing?checkout=cancelled"),
            ["client_reference_id"] = tenant.Id.ToString(),
            ["metadata[tenant_id]"] = tenant.Id.ToString(),
            ["subscription_data[metadata][tenant_id]"] = tenant.Id.ToString(),
            ["subscription_data[metadata][plan]"] = plan.ToString(),
            ["allow_promotion_codes"] = "true",
        };
        if (tenant.BillingProvider == "Stripe" && tenant.BillingCustomerId is { } cus) form["customer"] = cus;
        else form["customer_email"] = email;
        using var doc = await StripePostAsync("checkout/sessions", form, ct);
        return doc.RootElement.GetProperty("url").GetString()!;
    }

    public async Task<string> StripePortalUrlAsync(Tenant tenant, CancellationToken ct = default)
    {
        using var doc = await StripePostAsync("billing_portal/sessions", new()
        {
            ["customer"] = tenant.BillingCustomerId ?? throw new BillingException("No Stripe customer."),
            ["return_url"] = urls.Absolute("/Billing"),
        }, ct);
        return doc.RootElement.GetProperty("url").GetString()!;
    }

    private async Task<JsonDocument> StripePostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.stripe.com/v1/{path}") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Stripe.SecretKey);
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogError("Stripe {Path} failed: {Status} {Body}", path, (int)res.StatusCode, body);
            throw new BillingException("The payment provider rejected the request. Please try again or contact support.");
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>Verifies the Stripe-Signature header (HMAC-SHA256 over "timestamp.payload", 5-minute tolerance).</summary>
    public static bool VerifyStripeSignature(string payload, string? header, string secret, DateTimeOffset now, int toleranceSeconds = 300)
    {
        if (string.IsNullOrEmpty(header)) return false;
        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t") timestamp = kv[1];
            else if (kv[0] == "v1") signatures.Add(kv[1]);
        }
        if (timestamp is null || signatures.Count == 0 || !long.TryParse(timestamp, out var ts)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > toleranceSeconds) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return signatures.Any(s => FixedTimeEqualsHex(expected, s));
    }

    public async Task<WebhookResult> HandleStripeWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        if (!options.Stripe.Configured) return new(404, "Stripe not configured");
        if (!VerifyStripeSignature(payload, signature, options.Stripe.WebhookSecret!, clock.GetUtcNow()))
        {
            log.LogWarning("Rejected Stripe webhook with invalid signature");
            return new(400, "Invalid signature");
        }
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var eventId = root.GetProperty("id").GetString()!;
        var type = root.GetProperty("type").GetString()!;
        var obj = root.GetProperty("data").GetProperty("object");
        if (!await ClaimEventAsync($"stripe:{eventId}", ct)) return new(200, "Duplicate ignored");

        switch (type)
        {
            case "checkout.session.completed":
            {
                var tenant = await FindTenantAsync(Str(obj, "client_reference_id") ?? Meta(obj, "tenant_id"), null, null, ct);
                if (tenant is null) break;
                tenant.BillingProvider = "Stripe";
                tenant.BillingCustomerId = Str(obj, "customer");
                tenant.SubscriptionId = Str(obj, "subscription");
                tenant.SubscriptionStatus ??= "pending";
                Audit(tenant, "billing.checkout_completed", "Stripe checkout completed.");
                break;
            }
            case "customer.subscription.created":
            case "customer.subscription.updated":
            case "customer.subscription.deleted":
            {
                var tenant = await FindTenantAsync(Meta(obj, "tenant_id"), Str(obj, "id"), Str(obj, "customer"), ct);
                if (tenant is null) break;
                var priceId = obj.GetProperty("items").GetProperty("data")[0].GetProperty("price").GetProperty("id").GetString();
                var plan = options.Stripe.Prices.FirstOrDefault(kv => kv.Value == priceId).Key;
                var status = type == "customer.subscription.deleted" ? "canceled" : NormalizeStripe(Str(obj, "status"));
                // current_period_end moved onto subscription items in newer API versions; read either.
                var periodEnd = Long(obj, "current_period_end") ?? Long(obj.GetProperty("items").GetProperty("data")[0], "current_period_end");
                ApplySubscription(tenant, "Stripe", Str(obj, "customer"), Str(obj, "id"), status,
                    Enum.TryParse<Plan>(plan, out var p) ? p : null, periodEnd, obj.TryGetProperty("cancel_at_period_end", out var c) && c.GetBoolean());
                break;
            }
            default:
                log.LogDebug("Ignoring Stripe event {Type}", type);
                break;
        }
        await db.SaveChangesAsync(ct);
        return new(200, "OK");
    }

    private static string NormalizeStripe(string? s) => s switch
    {
        "active" or "trialing" => "active",
        "past_due" or "unpaid" => "past_due",
        "canceled" or "incomplete_expired" => "canceled",
        _ => "pending",
    };

    // ------------------------------------------------------------ Razorpay

    public async Task<string> RazorpaySubscribeUrlAsync(Tenant tenant, Plan plan, string email, CancellationToken ct = default)
    {
        var planId = options.Razorpay.Plans.GetValueOrDefault(plan.ToString()) ?? throw new BillingException($"No Razorpay plan configured for {plan}.");
        var body = new
        {
            plan_id = planId,
            total_count = 120, // monthly for up to 10 years; cancel any time
            quantity = 1,
            customer_notify = 1,
            notes = new { tenant_id = tenant.Id.ToString(), plan = plan.ToString(), email },
        };
        using var doc = await RazorpaySendAsync(HttpMethod.Post, "subscriptions", body, ct);
        var id = doc.RootElement.GetProperty("id").GetString()!;
        // Remember the pending subscription so the webhook can find the tenant even without notes.
        tenant.BillingProvider = "Razorpay";
        tenant.SubscriptionId = id;
        tenant.SubscriptionStatus = "pending";
        Audit(tenant, "billing.checkout_started", $"Razorpay subscription {id} created for {plan}.");
        await db.SaveChangesAsync(ct);
        return doc.RootElement.GetProperty("short_url").GetString()!;
    }

    public async Task RazorpayChangePlanAsync(Tenant tenant, Plan plan, CancellationToken ct = default)
    {
        var planId = options.Razorpay.Plans.GetValueOrDefault(plan.ToString()) ?? throw new BillingException($"No Razorpay plan configured for {plan}.");
        using var _ = await RazorpaySendAsync(HttpMethod.Patch, $"subscriptions/{tenant.SubscriptionId}", new { plan_id = planId, schedule_change_at = "now", customer_notify = 1 }, ct);
        Audit(tenant, "billing.plan_change_requested", $"Razorpay plan change to {plan} requested.");
        await db.SaveChangesAsync(ct);
    }

    public async Task RazorpayCancelAsync(Tenant tenant, CancellationToken ct = default)
    {
        using var _ = await RazorpaySendAsync(HttpMethod.Post, $"subscriptions/{tenant.SubscriptionId}/cancel", new { cancel_at_cycle_end = 1 }, ct);
        tenant.CancelAtPeriodEnd = true;
        Audit(tenant, "billing.cancel_requested", "Razorpay subscription set to cancel at the end of the billing cycle.");
        await db.SaveChangesAsync(ct);
    }

    private async Task<JsonDocument> RazorpaySendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, $"https://api.razorpay.com/v1/{path}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{options.Razorpay.KeyId}:{options.Razorpay.KeySecret}")));
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogError("Razorpay {Path} failed: {Status} {Body}", path, (int)res.StatusCode, text);
            throw new BillingException("The payment provider rejected the request. Please try again or contact support.");
        }
        return JsonDocument.Parse(text);
    }

    /// <summary>Razorpay signs the raw body with HMAC-SHA256 using the webhook secret (hex in X-Razorpay-Signature).</summary>
    public static bool VerifyRazorpaySignature(string payload, string? signature, string secret) =>
        !string.IsNullOrEmpty(signature) &&
        FixedTimeEqualsHex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)), signature);

    public async Task<WebhookResult> HandleRazorpayWebhookAsync(string payload, string? signature, string? eventIdHeader, CancellationToken ct)
    {
        if (!options.Razorpay.Configured) return new(404, "Razorpay not configured");
        if (!VerifyRazorpaySignature(payload, signature, options.Razorpay.WebhookSecret!))
        {
            log.LogWarning("Rejected Razorpay webhook with invalid signature");
            return new(400, "Invalid signature");
        }
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var type = Str(root, "event") ?? "";
        if (!root.TryGetProperty("payload", out var pl) || !pl.TryGetProperty("subscription", out var subWrap))
            return new(200, "Ignored");
        var sub = subWrap.GetProperty("entity");
        var subId = Str(sub, "id");
        var eventKey = eventIdHeader ?? $"{type}:{subId}:{Long(root, "created_at")}";
        if (!await ClaimEventAsync($"razorpay:{eventKey}", ct)) return new(200, "Duplicate ignored");

        var tenant = await FindTenantAsync(sub.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Object ? Str(notes, "tenant_id") : null, subId, null, ct);
        if (tenant is null) { await db.SaveChangesAsync(ct); return new(200, "Unknown subscription"); }

        var planId = Str(sub, "plan_id");
        var plan = options.Razorpay.Plans.FirstOrDefault(kv => kv.Value == planId).Key;
        var status = type switch
        {
            "subscription.activated" or "subscription.charged" or "subscription.resumed" or "subscription.updated" => "active",
            "subscription.pending" or "subscription.halted" => "past_due",
            "subscription.cancelled" or "subscription.completed" or "subscription.expired" => "canceled",
            "subscription.paused" => "past_due",
            _ => NormalizeRazorpay(Str(sub, "status")),
        };
        ApplySubscription(tenant, "Razorpay", Str(sub, "customer_id"), subId, status,
            Enum.TryParse<Plan>(plan, out var p) ? p : null, Long(sub, "current_end"), tenant.CancelAtPeriodEnd && status != "canceled");
        await db.SaveChangesAsync(ct);
        return new(200, "OK");
    }

    private static string NormalizeRazorpay(string? s) => s switch
    {
        "active" or "authenticated" => "active",
        "pending" or "halted" or "paused" => "past_due",
        "cancelled" or "completed" or "expired" => "canceled",
        _ => "pending",
    };

    // ------------------------------------------------------------ shared

    /// <summary>
    /// Single place where paid plans are granted or revoked. Active → the subscribed plan.
    /// Past due → keep the plan (grace) and show a banner. Canceled → back to Trial limits (data is kept).
    /// </summary>
    private void ApplySubscription(Tenant tenant, string provider, string? customerId, string? subscriptionId, string status, Plan? plan, long? periodEndUnix, bool cancelAtPeriodEnd)
    {
        // Ignore stale events about a subscription the tenant has since replaced.
        if (tenant.SubscriptionId is not null && subscriptionId is not null && tenant.SubscriptionId != subscriptionId && status == "canceled")
        {
            log.LogInformation("Ignoring cancellation of superseded subscription {Sub}", subscriptionId);
            return;
        }
        var before = $"{tenant.Plan}/{tenant.SubscriptionStatus}";
        tenant.BillingProvider = provider;
        tenant.BillingCustomerId = customerId ?? tenant.BillingCustomerId;
        tenant.SubscriptionId = subscriptionId ?? tenant.SubscriptionId;
        tenant.SubscriptionStatus = status;
        tenant.CancelAtPeriodEnd = cancelAtPeriodEnd;
        if (periodEndUnix is { } end) tenant.CurrentPeriodEnd = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        if (status == "active" && plan is { } paid && PaidPlans.Contains(paid)) tenant.Plan = paid;
        if (status == "canceled") { tenant.Plan = Plan.Trial; tenant.CancelAtPeriodEnd = false; }
        var after = $"{tenant.Plan}/{tenant.SubscriptionStatus}";
        if (before != after) Audit(tenant, "billing.subscription_changed", $"{provider}: {before} → {after}.");
    }

    private async Task<bool> ClaimEventAsync(string key, CancellationToken ct)
    {
        if (await db.ProcessedWebhookEvents.AnyAsync(e => e.Id == key, ct)) return false;
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { Id = key });
        return true;
    }

    private async Task<Tenant?> FindTenantAsync(string? tenantId, string? subscriptionId, string? customerId, CancellationToken ct)
    {
        Tenant? t = null;
        if (Guid.TryParse(tenantId, out var id)) t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null && subscriptionId is not null) t = await db.Tenants.FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId, ct);
        if (t is null && customerId is not null) t = await db.Tenants.FirstOrDefaultAsync(x => x.BillingCustomerId == customerId, ct);
        if (t is null) log.LogWarning("Billing webhook for unknown tenant (tenant {Tenant}, subscription {Sub})", tenantId, subscriptionId);
        return t;
    }

    private void Audit(Tenant tenant, string action, string details)
    {
        tenantContext.Use(tenant.Id, tenantContext.TenantId == tenant.Id ? tenantContext.UserName : "billing-webhook");
        audit.Record(action, details);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static string? Meta(JsonElement e, string key) => e.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object ? Str(m, key) : null;

    private static bool FixedTimeEqualsHex(byte[] expected, string hex)
    {
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hex)); }
        catch (FormatException) { return false; }
    }

    public static string FormatPeriodEnd(DateTime? d) => d?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
}
