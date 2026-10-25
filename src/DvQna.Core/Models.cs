using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvQna.Core;

/// <summary>A configured Dataverse environment (connection profile). Holds no secrets.</summary>
public sealed record EnvironmentProfile
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string TenantId { get; init; }
    public List<string> Tags { get; init; } = [];
    public string? AppInsightsResourceId { get; init; }
    public string? AppInsightsTenantId { get; init; }
    public PrivacyMode PrivacyMode { get; init; } = PrivacyMode.Operational;

    [JsonIgnore] public bool IsProd => Tags.Any(t => t.Equals("prod", StringComparison.OrdinalIgnoreCase));
    [JsonIgnore] public Uri BaseUri => new(Url.TrimEnd('/') + "/");
    [JsonIgnore] public string Scope => $"{BaseUri.GetLeftPart(UriPartial.Authority)}/.default";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrivacyMode { Metadata, Operational, Full }

/// <summary>Where a tool result came from; shown to the user as provenance.</summary>
public sealed record ToolSource(string Env, string Kind, string? Query, string? Window, DateTimeOffset FetchedAt);

/// <summary>Standard envelope every tool returns (design section 8.6).</summary>
public sealed class ToolResult
{
    public required string Summary { get; init; }
    /// <summary>Top rows sent to the LLM. The full set lives in the dataset store.</summary>
    public List<Dictionary<string, object?>> Data { get; init; } = [];
    public int RowCount { get; init; }
    public bool Truncated { get; init; }
    public string? DatasetId { get; set; }
    public List<ToolSource> Sources { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    /// <summary>Full rows for the UI and dataset store; never sent to the LLM.</summary>
    [JsonIgnore] public List<Dictionary<string, object?>>? FullData { get; init; }
    public UiHint? Ui { get; init; }

    public static ToolResult Error(string message) => new() { Summary = "Error: " + message, Warnings = [message] };
}

public sealed record UiHint(string Type, string? Title = null);

/// <summary>Obtains access tokens for a tenant and scope.</summary>
public interface ITokenProvider
{
    Task<string> GetTokenAsync(string tenantId, string[] scopes, CancellationToken ct);
}

public sealed class AppSettings
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataverseQnA");
    public AuthSettings Auth { get; set; } = new();
    public LlmSettings Llm { get; set; } = new();
}

public sealed class AuthSettings
{
    /// <summary>"msal" (default, shared multi-tenant public client) or "azcli" (developer fallback).</summary>
    public string Mode { get; set; } = "msal";
    public string? ClientId { get; set; }
}

public sealed class LlmSettings
{
    /// <summary>"foundry" (decided route) or "anthropic" (developer fallback using ANTHROPIC_API_KEY).</summary>
    public string Provider { get; set; } = "foundry";
    /// <summary>Foundry resource name (the subdomain of the endpoint).</summary>
    public string? FoundryResource { get; set; }
    public string Deployment { get; set; } = "claude-opus-5-5";
    /// <summary>"apikey" (key stored DPAPI-encrypted) or "entra" (Azure CLI / interactive credential).</summary>
    public string FoundryAuth { get; set; } = "entra";
    public string? FoundryTenantId { get; set; }
    public string Effort { get; set; } = "medium";
    public int MaxToolCallsPerTurn { get; set; } = 25;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
