using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SettleFlow.Core.Presets;

namespace SettleFlow.Web.Pages;

/// <summary>Serves the built-in demo files. Only fixed, generated files can be requested.</summary>
public sealed class SamplesModel : PageModel
{
    public IActionResult OnGet(string file)
    {
        var sample = SampleData.DriverSettlement().FirstOrDefault(f => f.FileName == file);
        if (sample is null) return NotFound();
        var type = file.EndsWith(".csv") ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return File(sample.Content, type, sample.FileName);
    }
}
