using System.Text.Json;
using Microsoft.AspNetCore.Html;
using SettleFlow.Web.Data;

namespace SettleFlow.Web.Services;

/// <summary>Renders schema.org structured data. Serialized (never string-built) and escaped so content can't break out of the script tag.</summary>
public static class JsonLd
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };

    public static IHtmlContent Script(object data) =>
        new HtmlString($"<script type=\"application/ld+json\">{JsonSerializer.Serialize(data, Options)}</script>");

    public static object Breadcrumbs(AppUrls urls, params (string Name, string Path)[] items) => new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "BreadcrumbList",
        ["itemListElement"] = items.Select((x, i) => new Dictionary<string, object>
        {
            ["@type"] = "ListItem", ["position"] = i + 1, ["name"] = x.Name, ["item"] = urls.Absolute(x.Path),
        }).ToArray(),
    };

    public static object Faq(IEnumerable<(string Q, string A)> items) => new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "FAQPage",
        ["mainEntity"] = items.Select(x => new Dictionary<string, object>
        {
            ["@type"] = "Question", ["name"] = x.Q,
            ["acceptedAnswer"] = new Dictionary<string, object> { ["@type"] = "Answer", ["text"] = x.A },
        }).ToArray(),
    };

    public static object Article(AppUrls urls, SeoPage page) => new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "Article",
        ["headline"] = page.Title,
        ["description"] = page.Description,
        ["datePublished"] = page.Updated.ToString("yyyy-MM-dd"),
        ["dateModified"] = page.Updated.ToString("yyyy-MM-dd"),
        ["mainEntityOfPage"] = urls.Absolute(page.Path),
        ["image"] = urls.Absolute("/img/og.png"),
        ["author"] = new Dictionary<string, object> { ["@type"] = "Organization", ["name"] = "Reconza", ["url"] = urls.Absolute("/") },
        ["publisher"] = new Dictionary<string, object>
        {
            ["@type"] = "Organization", ["name"] = "Reconza",
            ["logo"] = new Dictionary<string, object> { ["@type"] = "ImageObject", ["url"] = urls.Absolute("/img/apple-touch-icon.png") },
        },
    };

    public static object Software(AppUrls urls) => new Dictionary<string, object>
    {
        ["@context"] = "https://schema.org",
        ["@type"] = "SoftwareApplication",
        ["name"] = "Reconza",
        ["applicationCategory"] = "BusinessApplication",
        ["operatingSystem"] = "Web",
        ["url"] = urls.Absolute("/"),
        ["image"] = urls.Absolute("/img/og.png"),
        ["description"] = Seo.DefaultDescription,
        ["offers"] = Plans.Limits.Where(p => p.Key != Plan.Trial).Select(p => new Dictionary<string, object>
        {
            ["@type"] = "Offer", ["name"] = p.Value.Name,
            ["price"] = new string(p.Value.PriceIndia.Where(char.IsDigit).ToArray()), ["priceCurrency"] = "INR",
            ["url"] = urls.Absolute("/pricing"),
        }).Append(new Dictionary<string, object> { ["@type"] = "Offer", ["name"] = "Free plan", ["price"] = "0", ["priceCurrency"] = "INR" }).ToArray(),
    };
}
