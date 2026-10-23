using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SettleFlow.Core.Presets;
using SettleFlow.Web.Services;

namespace SettleFlow.Tests;

public sealed partial class SeoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "settleflow-seo-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public SeoTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_dir, "t.db")}");
            b.UseSetting("DataProtection:KeysPath", Path.Combine(_dir, "keys"));
            b.UseSetting("Ollama:Enabled", "false");
            b.UseSetting("Scanning:Mode", "Off");
            b.UseSetting("App:PublicUrl", "https://reconza.example");
        });
    }

    [Fact]
    public async Task Every_public_page_is_indexable_with_unique_metadata()
    {
        var c = _factory.CreateClient();
        var titles = new HashSet<string>();
        var descriptions = new HashSet<string>();
        foreach (var page in Seo.Pages)
        {
            var res = await c.GetAsync(page.Path);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{page.Path} returned {(int)res.StatusCode}");
            var html = await res.Content.ReadAsStringAsync();
            var title = WebUtility.HtmlDecode(TitleRx().Match(html).Groups[1].Value);
            var desc = WebUtility.HtmlDecode(MetaRx("description").Match(html).Groups[1].Value);
            Assert.True(titles.Add(title), $"Duplicate title on {page.Path}: {title}");
            Assert.True(descriptions.Add(desc), $"Duplicate description on {page.Path}");
            Assert.InRange(title.Length, 15, 75);
            Assert.InRange(desc.Length, 70, 160);
            Assert.Contains($"<link rel=\"canonical\" href=\"https://reconza.example{(page.Path == "/" ? "/" : page.Path)}\"", html);
            Assert.Contains("index,follow", html);
            Assert.Single(H1Rx().Matches(html));
            Assert.Contains("og:image", html);
            Assert.DoesNotContain("SettleFlow", html, StringComparison.OrdinalIgnoreCase); // old brand must never reappear
            Assert.Contains("Reconza", html);
            foreach (Match m in JsonLdRx().Matches(html))
                using (JsonDocument.Parse(m.Groups[1].Value)) { } // structured data must be valid JSON
        }
    }

    [Fact]
    public async Task Sitemap_and_robots()
    {
        var c = _factory.CreateClient();
        var sitemap = XDocument.Parse(await c.GetStringAsync("/sitemap.xml"));
        var locs = sitemap.Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value).ToList();
        Assert.Equal(Seo.Pages.Count, locs.Count);
        Assert.All(locs, l => Assert.StartsWith("https://reconza.example/", l));
        var robots = await c.GetStringAsync("/robots.txt");
        Assert.Contains("Sitemap: https://reconza.example/sitemap.xml", robots);
        Assert.Contains("Disallow: /Runs/", robots);
    }

    [Fact]
    public async Task App_pages_are_noindex()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Account/Login");
        Assert.Contains("noindex,nofollow", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
    }

    [Fact]
    public async Task Free_tool_compares_uploaded_files()
    {
        var c = _factory.CreateClient();
        var page = await c.GetStringAsync("/tools/compare-excel-files");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var files = SampleData.DriverSettlement();
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(files.Single(f => f.FileName == "trips.xlsx").Content), "fileA", "trips.xlsx" },
            { new ByteArrayContent(files.Single(f => f.FileName == "customer_shipments.xlsx").Content), "fileB", "customer_shipments.xlsx" },
        };
        var res = await c.PostAsync("/tools/compare-excel-files?handler=Upload", form);
        var html = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Results: trips.xlsx vs customer_shipments.xlsx", html);
        Assert.Contains("T-1015", html);       // only in A
        Assert.Contains("-1,250.00", html);    // short payment
    }

    [Fact]
    public async Task Canonical_redirects_merge_duplicate_urls()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var r = await c.GetAsync("/Pricing");
        Assert.Equal(HttpStatusCode.MovedPermanently, r.StatusCode);
        Assert.Equal("/pricing", r.Headers.Location?.OriginalString);
        r = await c.GetAsync("/guides/fuel-card-reconciliation/?utm_source=x");
        Assert.Equal("/guides/fuel-card-reconciliation?utm_source=x", r.Headers.Location?.OriginalString);

        var other = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://old-host.example/") });
        r = await other.GetAsync("/tools/compare-excel-files?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, r.StatusCode);
        Assert.Equal("https://reconza.example/tools/compare-excel-files?x=1", r.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/healthz")).StatusCode); // probes are never redirected
    }

    [Fact]
    public async Task Template_download_and_llms_txt()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync(Seo.TemplatePath);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using (var wb = new ClosedXML.Excel.XLWorkbook(await res.Content.ReadAsStreamAsync()))
            Assert.Contains(wb.Worksheets, w => w.Name == "Settlement");
        var llms = await c.GetStringAsync("/llms.txt");
        Assert.StartsWith("# Reconza", llms);
        Assert.Contains("https://reconza.example/tools/compare-excel-files", llms);
        Assert.Contains("\"@type\":\"WebSite\"", await c.GetStringAsync("/"));
    }

    [GeneratedRegex("<title>(.*?)</title>")] private static partial Regex TitleRx();
    private static Regex MetaRx(string name) => new($"<meta name=\"{name}\" content=\"(.*?)\"");
    [GeneratedRegex("<h1[ >]")] private static partial Regex H1Rx();
    [GeneratedRegex("<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)] private static partial Regex JsonLdRx();

    public void Dispose() { _factory.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }
}
