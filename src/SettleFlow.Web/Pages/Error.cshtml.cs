using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SettleFlow.Web.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    public int Code { get; private set; } = 500;

    public void OnGet(int? code) => Set(code);
    public void OnPost(int? code) => Set(code);

    private void Set(int? code)
    {
        Code = code ?? 500;
        Response.StatusCode = Code;
        // A short opaque reference for support; never exception details.
        RequestId = Code >= 500 ? (Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier)[..12] : null;
    }
}
