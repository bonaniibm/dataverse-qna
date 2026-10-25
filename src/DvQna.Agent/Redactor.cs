using System.Text.Json;
using System.Text.RegularExpressions;
using DvQna.Core;

namespace DvQna.Agent;

/// <summary>Shapes a tool result for the LLM according to the environment's privacy mode (design section 11.3).</summary>
public static partial class Redactor
{
    private static readonly HashSet<string> FreeTextKeys = new(StringComparer.OrdinalIgnoreCase)
        { "error", "message", "detail", "evidence", "regarding", "owner", "by", "exceptiondetails", "friendlymessage" };

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    // International format only (+CC ...), so timestamps, GUIDs and durations are left alone.
    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}[\s-]?\(?\d{1,4}\)?(?:[\s-]?\d{2,4}){2,4}(?!\w)")] private static partial Regex Phone();
    [GeneratedRegex(@"(?i)(password|pwd|secret|apikey|api_key|accountkey|sharedaccesssignature|sig)\s*[=:]\s*[^;\s""']+")] private static partial Regex Secret();
    [GeneratedRegex(@"(?i)bearer\s+[a-z0-9\-._~+/]+=*")] private static partial Regex Bearer();

    public static string ForLlm(ToolResult result, PrivacyMode mode)
    {
        var payload = new Dictionary<string, object?>
        {
            ["summary"] = Clean(result.Summary, mode, "summary"),
            ["row_count"] = result.RowCount,
            ["truncated"] = result.Truncated ? true : null,
            ["dataset_id"] = result.DatasetId,
            ["rows"] = result.Data.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Value is string s ? Clean(s, mode, kv.Key) : kv.Value)).ToList(),
            ["warnings"] = result.Warnings.Count == 0 ? null : result.Warnings,
            ["sources"] = result.Sources.Select(s => new { s.Env, s.Kind, s.Window }).ToList(),
        };
        return JsonSerializer.Serialize(payload, Json.Options);
    }

    internal static string? Clean(string? value, PrivacyMode mode, string key)
    {
        if (value is null || mode == PrivacyMode.Full) return value;
        if (mode == PrivacyMode.Metadata && FreeTextKeys.Contains(key)) return "[hidden: metadata privacy mode]";
        var v = Secret().Replace(value, m => m.Groups[1].Value + "=[redacted]");
        v = Bearer().Replace(v, "Bearer [redacted]");
        v = Email().Replace(v, "[email]");
        v = Phone().Replace(v, "[phone]");
        return v;
    }
}
