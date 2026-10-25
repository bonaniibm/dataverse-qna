using System.Text.Json;
using System.Text.Json.Nodes;
using DvQna.Connectors;
using DvQna.Core;
using DvQna.Storage;

namespace DvQna.Tools;

/// <summary>Everything a tool needs to run. Tools only ever get the read-only reader.</summary>
public sealed class ToolContext(ProfileStore profiles, IDataverseReader dataverse, AppInsightsReader appInsights, EnvironmentProfileCache profileCache)
{
    public ProfileStore Profiles { get; } = profiles;
    public IDataverseReader Dataverse { get; } = dataverse;
    public AppInsightsReader AppInsights { get; } = appInsights;
    public EnvironmentProfileCache ProfileCache { get; } = profileCache;

    public EnvironmentProfile Env(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            var all = Profiles.All();
            if (all.Count == 1) return all[0];
            throw new ToolInputException("Parameter 'env' is required. Configured environments: " + string.Join(", ", all.Select(p => p.Name)));
        }
        return Profiles.Find(name) ?? throw new ToolInputException(
            $"Unknown environment '{name}'. Configured: " + string.Join(", ", Profiles.All().Select(p => p.Name)));
    }
}

public sealed class ToolInputException(string message) : Exception(message);

/// <summary>Typed accessors over the JSON input the model sent.</summary>
public sealed class ToolArgs(JsonElement root)
{
    public JsonElement Root { get; } = root;

    public string? Str(string name) =>
        Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()) : null;

    public string Required(string name) => Str(name) ?? throw new ToolInputException($"Parameter '{name}' is required.");

    public int Int(string name, int @default, int min, int max)
    {
        if (Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return Math.Clamp(i, min, max);
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return Math.Clamp(s, min, max);
        }
        return @default;
    }

    public List<string> List(string name) =>
        Root.ValueKind == JsonValueKind.Object && Root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList()
            : [];
}

public abstract class Tool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    /// <summary>JSON Schema for the input object.</summary>
    public abstract JsonObject Schema { get; }
    public abstract Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct);
}

/// <summary>Small JSON Schema builder so tool definitions stay readable.</summary>
public static class S
{
    public static JsonObject Obj(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (n, s, r) in props)
        {
            properties[n] = s;
            if (r) required.Add(n);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    public static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    public static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    public static JsonObject StrList(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };
    public static JsonObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) };

    public static (string, JsonObject, bool) Env(bool required = true) => ("env", Str("Environment profile name (see list_environments)."), required);
    public static (string, JsonObject, bool) Hours(int def, int max) => ("window_hours", Int($"Look-back window in hours (default {def}, max {max})."), false);
}

/// <summary>Helpers for building results consistently.</summary>
public static class Results
{
    public const int DefaultTopN = 25;

    public static ToolResult Table(string summary, List<Dictionary<string, object?>> rows, ToolSource source,
        IEnumerable<string>? warnings = null, int topN = DefaultTopN, string uiTitle = "", bool truncatedSource = false)
    {
        var w = warnings?.ToList() ?? [];
        if (truncatedSource) w.Add("Source query hit its row cap, so totals may be understated.");
        return new ToolResult
        {
            Summary = summary,
            Data = rows.Take(topN).ToList(),
            RowCount = rows.Count,
            Truncated = rows.Count > topN || truncatedSource,
            FullData = rows,
            Sources = [source],
            Warnings = w,
            Ui = new UiHint("table", uiTitle),
        };
    }

    public static ToolSource Src(EnvironmentProfile env, string kind, string? query = null, string? window = null) =>
        new(env.Name, kind, query, window, DateTimeOffset.UtcNow);

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static string Window(int hours) => $"{Iso(DateTimeOffset.UtcNow.AddHours(-hours))}/{Iso(DateTimeOffset.UtcNow)}";

    public static string? S(this Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static string Label(this Dictionary<string, object?> row, string key) => row.S(key + "@label") ?? row.S(key) ?? "";

    public static DateTimeOffset? Date(this Dictionary<string, object?> row, string key) =>
        DateTimeOffset.TryParse(row.S(key), out var d) ? d : null;

    public static double? Num(this Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null && double.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static string Esc(string s) => s.Replace("'", "''");

    public static string FirstLine(string? s, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return line.Length > max ? line[..max] + "…" : line;
    }
}
