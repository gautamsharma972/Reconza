using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SettleFlow.Core.Model;

namespace SettleFlow.Core.Mapping;

public sealed class OllamaOptions
{
    public bool Enabled { get; set; }
    /// <summary>Base URL of the Ollama server. Headers and a few sample rows are sent here, so it must be one you control.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3.2:3b";
    public int TimeoutSeconds { get; set; } = 45;
    public int SampleRows { get; set; } = 5;
}

/// <summary>
/// Rules first, then a local open-source LLM (via Ollama) to fill gaps and resolve ambiguous headers.
/// The model only proposes column names; every proposal is checked against the real headers, the
/// one-column-per-field rule and the sample values' type before it is shown. If Ollama is unreachable
/// or answers nonsense, the rule-based suggestions are returned unchanged.
/// </summary>
public sealed class HybridMappingSuggester(HttpClient http, OllamaOptions options) : IMappingSuggester
{
    private const double ExactRuleConfidence = 0.95;

    public async Task<SuggestionResult> SuggestAsync(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken ct = default)
    {
        var rules = HeuristicMappingSuggester.Suggest(fields, headers, sampleRows);
        if (!options.Enabled || headers.Count == 0) return new SuggestionResult(rules);

        List<LlmMapping> llm;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            llm = await AskAsync(fields, headers, sampleRows, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return new SuggestionResult(rules, $"AI suggestions unavailable ({Describe(ex)}); showing rule-based suggestions.");
        }

        return new SuggestionResult(Merge(fields, headers, sampleRows, rules, llm, options.Model));
    }

    /// <summary>Combines rule and model suggestions. Exact rule matches always win; the model never maps one column twice.</summary>
    public static IReadOnlyList<MappingSuggestion> Merge(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, IReadOnlyList<MappingSuggestion> rules, IReadOnlyList<LlmMapping> llm, string model)
    {
        var byField = rules.ToDictionary(r => r.Field, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, MappingSuggestion>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rules.Where(r => r.Column is not null && r.Confidence >= ExactRuleConfidence))
        {
            result[r.Field] = r;
            used.Add(r.Column!);
        }

        foreach (var m in llm.OrderByDescending(m => m.Confidence))
        {
            var field = fields.FirstOrDefault(f => f.Name.Equals(m.Field, StringComparison.OrdinalIgnoreCase));
            if (field is null || result.ContainsKey(field.Name) || string.IsNullOrWhiteSpace(m.Column)) continue;
            var col = headers.FirstOrDefault(h => h.Equals(m.Column.Trim(), StringComparison.OrdinalIgnoreCase));
            if (col is null || used.Contains(col)) continue; // hallucinated or already taken
            var idx = headers.ToList().IndexOf(col);
            var values = sampleRows.Select(r => idx < r.Count ? r[idx] : "").ToList();
            if (HeuristicMappingSuggester.ShapeBonus(field.Type, values) < 0) continue; // e.g. text column proposed for a Number field
            var confidence = Math.Clamp(m.Confidence, 0, 0.9);
            var rule = byField.GetValueOrDefault(field.Name);
            if (confidence < 0.5 && rule?.Column is not null) continue;
            var agrees = rule?.Column?.Equals(col, StringComparison.OrdinalIgnoreCase) == true;
            result[field.Name] = new MappingSuggestion(field.Name, col,
                agrees ? Math.Max(confidence, rule!.Confidence) : confidence,
                agrees ? "Rules and AI agree" : $"Suggested by {model}" + (string.IsNullOrWhiteSpace(m.Reason) ? "" : $": {m.Reason}"),
                agrees ? "Rules + AI" : "AI");
            used.Add(col);
        }

        foreach (var r in rules)
        {
            if (result.ContainsKey(r.Field)) continue;
            result[r.Field] = r.Column is not null && !used.Contains(r.Column)
                ? r
                : new MappingSuggestion(r.Field, null, 0, "No similar column");
            if (r.Column is not null) used.Add(r.Column);
        }
        return fields.Select(f => result[f.Name]).ToList();
    }

    private async Task<List<LlmMapping>> AskAsync(IReadOnlyList<FieldDef> fields, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows, CancellationToken ct)
    {
        static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
        var sample = sampleRows.Take(options.SampleRows).Select(r => headers.Select((h, i) => new { h, v = i < r.Count ? Clip(r[i]) : "" })
            .ToDictionary(x => x.h, x => x.v)).ToList();

        var prompt = $$"""
            You map spreadsheet columns to business fields for a reconciliation tool.

            Target fields (name, type, description):
            {{string.Join("\n", fields.Select(f => $"- {f.Name} ({f.Type}): {f.Label}" + (f.Synonyms.Count > 0 ? $"; often called {string.Join(", ", f.Synonyms.Take(8))}" : "")))}}

            Column headers in the uploaded file:
            {{JsonSerializer.Serialize(headers)}}

            First rows of the file:
            {{JsonSerializer.Serialize(sample)}}

            For every target field, pick the ONE column header (copied exactly) that holds that field, or null if none does.
            A column can be used for at most one field. Prefer null over a guess. Give a confidence from 0 to 1 and a short reason.
            """;

        var schema = new
        {
            type = "object",
            properties = new
            {
                mappings = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            field = new { type = "string", @enum = fields.Select(f => f.Name).ToArray() },
                            column = new { type = new[] { "string", "null" } },
                            confidence = new { type = "number" },
                            reason = new { type = "string" },
                        },
                        required = new[] { "field", "column", "confidence" },
                    },
                },
            },
            required = new[] { "mappings" },
        };

        var request = new
        {
            model = options.Model,
            stream = false,
            format = schema,
            options = new { temperature = 0 },
            messages = new[] { new { role = "user", content = prompt } },
        };

        using var response = await http.PostAsJsonAsync(new Uri(new Uri(options.BaseUrl), "/api/chat"), request, ct);
        response.EnsureSuccessStatusCode();
        var chat = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct)
                   ?? throw new JsonException("Empty response");
        var parsed = JsonSerializer.Deserialize<LlmAnswer>(chat.Message?.Content ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new JsonException("Unparseable answer");
        return parsed.Mappings ?? new();
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        HttpRequestException { StatusCode: { } code } => $"server returned {(int)code}",
        HttpRequestException => "server not reachable",
        _ => "unexpected answer"
    };

    public sealed record LlmMapping(string Field, string? Column, double Confidence, string? Reason);
    private sealed record LlmAnswer(List<LlmMapping>? Mappings);
    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);

    /// <summary>Quick reachability + model check for the settings page.</summary>
    public async Task<string> HealthAsync(CancellationToken ct = default)
    {
        if (!options.Enabled) return "Off";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(options.BaseUrl), "/api/tags"), cts.Token));
            var names = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
            return names.Contains(options.Model) ? $"Ready ({options.Model})" : $"Model '{options.Model}' not pulled — run: ollama pull {options.Model}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return $"Unreachable at {options.BaseUrl}";
        }
    }
}
