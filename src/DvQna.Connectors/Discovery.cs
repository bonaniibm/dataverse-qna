using System.Net.Http.Headers;
using System.Text.Json;
using DvQna.Core;

namespace DvQna.Connectors;

public sealed record DiscoveredEnvironment(string FriendlyName, string UniqueName, string Url, string TenantId, string? Region, string? Version, string? State);

/// <summary>Lists environments the signed-in user can access (Global Discovery Service v2).</summary>
public sealed class GlobalDiscovery(ITokenProvider tokens)
{
    private const string Endpoint = "https://globaldisco.crm.dynamics.com";
    private static readonly HttpClient Http = new();

    public async Task<List<DiscoveredEnvironment>> ListAsync(string tenantId, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(tenantId, [Endpoint + "/.default"], ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint + "/api/discovery/v2.0/Instances");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Discovery failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(e => new DiscoveredEnvironment(
            S(e, "FriendlyName") ?? "", S(e, "UniqueName") ?? "", S(e, "Url") ?? "", S(e, "TenantId") ?? tenantId,
            S(e, "Region"), S(e, "Version"), e.TryGetProperty("State", out var st) ? st.ToString() : null)).ToList();
    }

    private static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Finds the tenant that owns an environment from the 401 challenge, without signing in.</summary>
    public static async Task<string?> ResolveTenantAsync(string orgUrl, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(orgUrl.TrimEnd('/') + "/api/data/v9.2/", ct);
        var header = resp.Headers.WwwAuthenticate.ToString();
        var marker = "login.microsoftonline.com/";
        var i = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = header[(i + marker.Length)..];
        return rest[..rest.IndexOf('/')];
    }
}
