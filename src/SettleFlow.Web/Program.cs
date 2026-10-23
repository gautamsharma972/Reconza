using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SettleFlow.Core.Mapping;
using SettleFlow.Web.Data;
using SettleFlow.Web.Services;

var builder = WebApplication.CreateBuilder(args);
// Server-specific settings and secrets (see appsettings.Server.example.json). Lives only on the server, so
// uploading a new release never overwrites it. Tests must never load a developer's or server's real credentials.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Configuration.AddJsonFile("appsettings.Server.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
var config = builder.Configuration;
var isProd = builder.Environment.IsProduction();
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);

// Railway and similar platforms tell the app which port to listen on.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://[::]:{port}");

T Bind<T>(string section) where T : new() => config.GetSection(section).Get<T>() ?? new T();

// Render (and similar hosts) expose the service's public URL; use it unless App:PublicUrl is set explicitly.
if (string.IsNullOrEmpty(config["App:PublicUrl"]) && Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL") is { Length: > 0 } renderUrl)
    config["App:PublicUrl"] = renderUrl;

// ---------------------------------------------------------------- database
// Database:Provider = Sqlite (default; a file in App_Data), MySql (Plesk/MariaDB) or PostgreSQL (DATABASE_URL).
// Each has its own versioned migrations, applied automatically at startup.
var postgres = config.GetConnectionString("Postgres") is { Length: > 0 } pg ? pg
    : config["DATABASE_URL"] is { Length: > 0 } url ? DatabaseUrl.ToNpgsql(url) : null;
var dbProvider = postgres is not null ? "PostgreSQL" : (config["Database:Provider"] ?? "Sqlite");
var mysql = config.GetConnectionString("MySql");
if (dbProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(mysql) || mysql.Contains("CHANGE_ME") || mysql.Contains("<")))
    throw new InvalidOperationException("Database:Provider is MySql: set ConnectionStrings:MySql to your MySQL/MariaDB details (Server=…;Database=…;User=…;Password=…).");
if (isProd && postgres is null && !dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) && !dbProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Database:Provider must be Sqlite or MySql (or set DATABASE_URL for PostgreSQL).");
if (isProd && postgres is null && string.IsNullOrEmpty(config["Database:Provider"]))
    throw new InvalidOperationException("Production needs a database: set Database:Provider (Sqlite or MySql) or DATABASE_URL (PostgreSQL).");

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
if (postgres is not null)
{
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(postgres, n => n.EnableRetryOnFailure(3)));
}
else if (dbProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<MySqlAppDbContext>(o => o
        .UseMySQL(mysql!, m => m.EnableRetryOnFailure(3))
        .AddInterceptors(new MariaDbCompatibilityInterceptor())); // MySQL 8 and MariaDB
    builder.Services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<MySqlAppDbContext>());
}
else
{
    var sqlitePath = config.GetConnectionString("Sqlite") ?? $"Data Source={Path.Combine(dataDir, "reconza.db")}";
    builder.Services.AddDbContext<SqliteAppDbContext>(o => o.UseSqlite(sqlitePath));
    builder.Services.AddScoped<AppDbContext>(sp => sp.GetRequiredService<SqliteAppDbContext>());
}
var serverDatabase = postgres is not null || dbProvider.Equals("MySql", StringComparison.OrdinalIgnoreCase);

// ---------------------------------------------------------------- identity
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(3));
builder.Services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddClaimsPrincipalFactory<TenantClaimsFactory>()
    .AddDefaultTokenProviders()
    .AddTokenProvider<InviteTokenProvider>(InviteTokenProvider.ProviderName);

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.LogoutPath = "/Account/Logout";
    o.Cookie.Name = "rz.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = isProd ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
});

// ---------------------------------------------------------------- data protection (file + cookie encryption keys)
// The key ring is encrypted at rest with a master key from configuration (required in production).
var masterKey = config["DataProtection:MasterKey"] is { Length: > 0 } mk ? MasterKey.FromBase64(mk)
    : isProd ? throw new InvalidOperationException("Set DataProtection:MasterKey (e.g. `openssl rand -base64 32`). Keep a copy safe: losing it makes stored files unreadable.")
    : null;
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("SettleFlow");
if (serverDatabase) dataProtection.PersistKeysToDbContext<AppDbContext>(); // in the database, encrypted with the master key
else dataProtection.PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeysPath"] ?? Path.Combine(dataDir, "keys")));
if (masterKey is not null)
{
    builder.Services.AddSingleton(masterKey);
    builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(masterKey.Bytes));
}

// ---------------------------------------------------------------- storage
var storageProvider = config["Storage:Provider"] ?? "Local";
if (storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(Bind<S3Options>("Storage:S3"));
    builder.Services.AddSingleton<S3FileStore>();
    builder.Services.AddSingleton<IFileStore>(sp => sp.GetRequiredService<S3FileStore>());
}
else if (storageProvider.Equals("Database", StringComparison.OrdinalIgnoreCase))
{
    if (!serverDatabase) throw new InvalidOperationException("Storage:Provider=Database requires PostgreSQL or MySQL.");
    builder.Services.AddSingleton<IFileStore, DatabaseFileStore>();
}
else
{
    if (isProd && !config.GetValue<bool>("Storage:AllowLocalInProduction"))
        throw new InvalidOperationException("Production requires Storage:Provider=S3 or Database (local disk is lost on redeploy).");
    builder.Services.AddSingleton<IFileStore, EncryptedLocalFileStore>();
}

// ---------------------------------------------------------------- app services
var company = Bind<CompanyOptions>("Company");
if (isProd && company.Missing().ToList() is { Count: > 0 } missingCompany)
    throw new InvalidOperationException("Fill in the business details shown on the legal pages: " + string.Join(", ", missingCompany) + " (appsettings.Production.json).");
builder.Services.AddSingleton(company);
var appOptions = Bind<AppOptions>("App");
if (string.IsNullOrEmpty(appOptions.SupportEmail) && !string.IsNullOrEmpty(company.Email)) appOptions.SupportEmail = company.Email;
builder.Services.AddSingleton(appOptions);
builder.Services.AddSingleton<AppUrls>();
var seoOptions = Bind<SeoOptions>("Seo");
builder.Services.AddSingleton(seoOptions);
var emailOptions = Bind<EmailOptions>("Email");
if (isProd && emailOptions.ProductionErrors().ToList() is { Count: > 0 } emailErrors)
    throw new InvalidOperationException("Fix the production email configuration: " + string.Join(" ", emailErrors));
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton<IEmailSender, EmailSender>();
builder.Services.AddSingleton(Bind<OllamaOptions>("Ollama"));
builder.Services.AddHttpClient<HybridMappingSuggester>(c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddTransient<IMappingSuggester>(sp => sp.GetRequiredService<HybridMappingSuggester>());
builder.Services.AddSingleton(Bind<ScanOptions>("Scanning"));
builder.Services.AddSingleton<IMalwareScanner, ClamAvScanner>();
builder.Services.AddSingleton(Bind<WorkerOptions>("Workers"));
builder.Services.AddSingleton<RunQueueSignal>();
builder.Services.AddHostedService<RunWorker>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(Bind<BillingOptions>("Billing"));
builder.Services.AddHttpClient<BillingService>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddMemoryCache(o => o.SizeLimit = config.GetValue("Tools:CacheMb", 256) * 1024L * 1024); // free-tool files, bounded
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<UploadInspector>();
builder.Services.AddScoped<RunService>();
builder.Services.AddHostedService<RetentionService>();

if (isProd && Bind<BillingOptions>("Billing").AllowManualPlanChange)
    throw new InvalidOperationException("Billing:AllowManualPlanChange must be false in production.");
if (isProd && (string.IsNullOrWhiteSpace(appOptions.PublicUrl) || !appOptions.PublicUrl.StartsWith("https://") || appOptions.PublicUrl.Contains("localhost")))
    throw new InvalidOperationException("Set App:PublicUrl to the site's public https:// address (appsettings.Server.json).");

// ---------------------------------------------------------------- platform
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind Railway/AWS load balancers TLS terminates at the proxy.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
// HTTPS-only for a year once a browser has seen the site (HSTS). Preload is left off until the bare domain
// also serves HTTPS with this header (a requirement of the browser preload list).
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
    o.Preload = false;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in, sign-up, password reset and invites: 20 requests per minute per client IP.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    // Free comparison tool: 40 requests per 10 minutes per client IP.
    o.AddPolicy("tool", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 40, Window = TimeSpan.FromMinutes(10) }));
});
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true; // applied only to signed-out traffic (see UseWhen below) to avoid BREACH-style leaks of signed-in secrets
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
});
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);

const long maxRequest = 210L * 1024 * 1024; // hard ceiling; per-plan limits are enforced per file
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxRequest);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = maxRequest;
    k.AddServerHeader = false; // don't advertise the server stack
});
builder.Services.AddAntiforgery(o => { o.Cookie.Name = "rz.csrf"; o.Cookie.SecurePolicy = isProd ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; });
builder.Services.Configure<Microsoft.AspNetCore.Mvc.CookieTempDataProviderOptions>(o =>
{
    o.Cookie.Name = "rz.msg"; // one-time notices ("Saved.", "Invitation sent")
    o.Cookie.SecurePolicy = isProd ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToFolder("/Account");
    o.Conventions.AllowAnonymousToPage("/Index");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Samples");
    o.Conventions.AllowAnonymousToFolder("/Site");
});

var app = builder.Build();

// ---------------------------------------------------------------- schema
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (config.GetValue("Database:AutoMigrate", true))
    {
        await db.Database.MigrateAsync(); // versioned migrations, applied automatically
    }
    else
    {
        // Schema is managed manually (e.g. reconza-database.sql imported in phpMyAdmin): never alter it here,
        // just refuse to run against an out-of-date schema. Reading the history table takes no locks.
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException("The database schema is out of date (pending: " + string.Join(", ", pending)
                + "). Import the SQL script shipped with this release, or set Database:AutoMigrate to true.");
    }
}

app.UseForwardedHeaders();

// SEO: one canonical URL per page. Redirect other host names (non-www, temporary hosting domains) to App:PublicUrl,
// and /Pricing or /pricing/ to /pricing, with a permanent 301 so search engines merge ranking signals.
var canonicalBase = Uri.TryCreate(appOptions.PublicUrl, UriKind.Absolute, out var canonicalUri) ? canonicalUri : null;
app.Use(async (ctx, next) =>
{
    var req = ctx.Request;
    if ((HttpMethods.IsGet(req.Method) || HttpMethods.IsHead(req.Method))
        && !req.Path.StartsWithSegments("/healthz") && !req.Path.StartsWithSegments("/readyz") && !req.Path.StartsWithSegments("/webhooks"))
    {
        var host = req.Host.Host;
        var local = host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost") || host.EndsWith(".test");
        if (canonicalBase is not null && !local && !string.Equals(host, canonicalBase.Host, StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect(canonicalBase.GetLeftPart(UriPartial.Authority) + req.PathBase + req.Path + req.QueryString, permanent: true);
            return;
        }
        var path = req.Path.Value ?? "/";
        if (path.Length > 1 && Seo.For(path) is { } page && page.Path != path)
        {
            ctx.Response.Redirect(page.Path + req.QueryString, permanent: true);
            return;
        }
    }
    await next();
});
// Compress pages for signed-out visitors (marketing pages, which search engines measure for speed).
// Signed-in app pages stay uncompressed so per-user secrets can't leak through compression side channels.
app.UseWhen(ctx => !ctx.Request.Cookies.ContainsKey("rz.auth"), branch => branch.UseResponseCompression());
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error/500");
    app.UseStatusCodePagesWithReExecute("/Error/{0}");
    app.UseHsts();
    app.UseHttpsRedirection();
}

var analytics = !string.IsNullOrEmpty(seoOptions.CloudflareAnalyticsToken);
var csp = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; "
    + (analytics ? "script-src 'self' https://static.cloudflareinsights.com; connect-src 'self' https://cloudflareinsights.com; " : "script-src 'self'; ")
    + "frame-ancestors 'none'; form-action 'self' https://checkout.stripe.com https://billing.stripe.com https://rzp.io https://*.razorpay.com";
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.Remove("X-Powered-By");
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] = csp;
    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false }); // liveness
app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

// Payment webhooks: anonymous, raw body, signature-verified inside BillingService.
app.MapPost("/webhooks/stripe", async (HttpRequest req, BillingService billing, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body);
    var r = await billing.HandleStripeWebhookAsync(await reader.ReadToEndAsync(ct), req.Headers["Stripe-Signature"], ct);
    return Results.Text(r.Message, statusCode: r.StatusCode);
}).DisableAntiforgery();
app.MapPost("/webhooks/razorpay", async (HttpRequest req, BillingService billing, CancellationToken ct) =>
{
    using var reader = new StreamReader(req.Body);
    var r = await billing.HandleRazorpayWebhookAsync(await reader.ReadToEndAsync(ct), req.Headers["X-Razorpay-Signature"], req.Headers["X-Razorpay-Event-Id"], ct);
    return Results.Text(r.Message, statusCode: r.StatusCode);
}).DisableAntiforgery();

// Search engines: crawl the marketing site, not the app.
app.MapGet("/robots.txt", (AppUrls urls) => Results.Text($"""
    User-agent: *
    Allow: /
    Disallow: /Account/
    Disallow: /Runs/
    Disallow: /Workflows/
    Disallow: /Settings
    Disallow: /Billing
    Disallow: /Audit
    Disallow: /Samples
    Disallow: /webhooks/

    Sitemap: {urls.Absolute("/sitemap.xml")}
    """, "text/plain"));
app.MapGet("/sitemap.xml", (AppUrls urls) =>
{
    var x = System.Xml.Linq.XNamespace.Get("http://www.sitemaps.org/schemas/sitemap/0.9");
    var doc = new System.Xml.Linq.XElement(x + "urlset", Seo.Pages.Select(p => new System.Xml.Linq.XElement(x + "url",
        new System.Xml.Linq.XElement(x + "loc", urls.Absolute(p.Path)),
        new System.Xml.Linq.XElement(x + "lastmod", p.Updated.ToString("yyyy-MM-dd")),
        new System.Xml.Linq.XElement(x + "changefreq", p.ChangeFrequency),
        new System.Xml.Linq.XElement(x + "priority", p.Priority.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)))));
    return Results.Text("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + doc, "application/xml");
});

// Free lead-magnet download (generated once per process; it has no user data).
var settlementTemplate = new Lazy<byte[]>(SettleFlow.Core.Reporting.SettlementTemplate.DriverSettlement);
app.MapGet(Seo.TemplatePath, () => Results.File(settlementTemplate.Value,
    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "driver-settlement-template-reconza.xlsx"));

// Plain-text site summary for AI search assistants (llmstxt.org convention).
app.MapGet("/llms.txt", (AppUrls urls) => Results.Text(
    "# Reconza\n\n> " + Seo.DefaultDescription + "\n\n"
    + "Reconza is web software for transporters, fleets, distributors and online sellers in India and elsewhere. "
    + "It reconciles the Excel and CSV files a business already receives (trip registers, fuel card statements, deductions, "
    + "customer shipment reports, marketplace settlement and fee reports), flags duplicates, missing records and amount "
    + "mismatches with deterministic rules, and produces a settlement workbook and per-driver statements. Plans start at "
    + "₹999/month (US$19 outside India) with a permanent free plan.\n\n## Pages\n\n"
    + string.Join("\n", Seo.Pages.Select(p => $"- [{p.Title}]({urls.Absolute(p.Path)}): {p.Description}"))
    + $"\n\n## Free resources\n\n- [Driver settlement Excel template]({urls.Absolute(Seo.TemplatePath)})\n",
    "text/plain; charset=utf-8"));

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

public partial class Program;
