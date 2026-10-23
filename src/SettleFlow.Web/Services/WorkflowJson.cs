using System.Text.Json;
using System.Text.Json.Serialization;
using SettleFlow.Core.Model;

namespace SettleFlow.Web.Services;

public static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static WorkflowDefinition Deserialize(string json)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(json, Options) ?? new WorkflowDefinition();
        // Restore case-insensitive lookups lost in round-tripping.
        foreach (var s in def.Sources) s.ColumnMap = new(s.ColumnMap, StringComparer.OrdinalIgnoreCase);
        def.Report.SourceLabels = new(def.Report.SourceLabels, StringComparer.OrdinalIgnoreCase);
        return def;
    }
}
