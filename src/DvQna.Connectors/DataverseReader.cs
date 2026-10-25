using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DvQna.Core;
using Microsoft.Extensions.Logging;

namespace DvQna.Connectors;

/// <summary>
/// Read-only access to the Dataverse Web API (design section 11.4).
/// Only GET is possible: the HTTP pipeline rejects any other method, and only allow-listed functions can be invoked.
/// </summary>
public interface IDataverseReader
{
    /// <summary>OData query against an entity set. Follows nextLink until <paramref name="maxRows"/>.</summary>
    Task<QueryResult> QueryAsync(EnvironmentProfile env, string entitySet, string query, int maxRows, CancellationToken ct);
    Task<QueryResult> FetchXmlAsync(EnvironmentProfile env, string entitySet, string fetchXml, int maxRows, CancellationToken ct);
    /// <summary>Invoke an allow-listed read-only function, e.g. <c>RetrieveTotalRecordCount(EntityNames=['account'])</c>.</summary>
    Task<JsonElement> FunctionAsync(EnvironmentProfile env, string functionCall, CancellationToken ct);
    /// <summary>Raw GET of a relative Web API path (metadata, single records).</summary>
    Task<JsonElement> GetAsync(EnvironmentProfile env, string relativePath, CancellationToken ct);
}

public sealed record QueryResult(List<Dictionary<string, object?>> Rows, bool Truncated, string Url);

public sealed class DataverseReader : IDataverseReader
{
    public const int MaxConcurrencyPerEnvironment = 4;

    /// <summary>Read-only Dataverse functions the tool may call. Anything else throws.</summary>
    public static readonly IReadOnlySet<string> AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WhoAmI", "RetrieveVersion", "RetrieveTotalRecordCount", "RetrieveRolePrivilegesRole",
        "RetrieveUserPrivileges", "RetrievePrincipalAccess", "RetrieveCurrentOrganization",
    };

    private const string FormattedValue = "@OData.Community.Display.V1.FormattedValue";
    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly ILogger<DataverseReader> _log;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public DataverseReader(ITokenProvider tokens, ILogger<DataverseReader> log)
    {
        _tokens = tokens;
        _log = log;
        _http = new HttpClient(new ReadOnlyHandler(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }))
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DataverseQnA/0.1");
    }

    public async Task<QueryResult> QueryAsync(EnvironmentProfile env, string entitySet, string query, int maxRows, CancellationToken ct)
    {
        var url = $"api/data/v9.2/{entitySet}" + (string.IsNullOrEmpty(query) ? "" : "?" + query.TrimStart('?'));
        return await PageAsync(env, url, maxRows, ct);
    }

    public Task<QueryResult> FetchXmlAsync(EnvironmentProfile env, string entitySet, string fetchXml, int maxRows, CancellationToken ct)
    {
        var url = $"api/data/v9.2/{entitySet}?fetchXml={Uri.EscapeDataString(fetchXml)}";
        return PageAsync(env, url, maxRows, ct);
    }

    public async Task<JsonElement> FunctionAsync(EnvironmentProfile env, string functionCall, CancellationToken ct)
    {
        // Bound calls look like systemusers(id)/Microsoft.Dynamics.CRM.RetrieveUserPrivileges()
        var name = functionCall.Split('?')[0].Split('/').Last().Split('(')[0].Replace("Microsoft.Dynamics.CRM.", "");
        if (!AllowedFunctions.Contains(name))
            throw new InvalidOperationException($"Function '{name}' is not on the read-only allow-list.");
        return await GetAsync(env, $"api/data/v9.2/{functionCall}", ct);
    }

    public async Task<JsonElement> GetAsync(EnvironmentProfile env, string relativePath, CancellationToken ct)
    {
        using var doc = await SendAsync(env, relativePath.StartsWith("api/") ? relativePath : "api/data/v9.2/" + relativePath, null, ct);
        return doc.RootElement.Clone();
    }

    private async Task<QueryResult> PageAsync(EnvironmentProfile env, string url, int maxRows, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        string? next = url;
        var truncated = false;
        var pageSize = Math.Clamp(maxRows, 1, 5000);
        while (next is not null)
        {
            using var doc = await SendAsync(env, next, pageSize, ct);
            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (rows.Count >= maxRows) { truncated = true; break; }
                rows.Add(Flatten(item));
            }
            if (truncated) break;
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            if (next is not null && rows.Count >= maxRows) { truncated = true; break; }
        }
        return new QueryResult(rows, truncated, url);
    }

    private async Task<JsonDocument> SendAsync(EnvironmentProfile env, string relativeOrAbsolute, int? pageSize, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd(env.Url, _ => new SemaphoreSlim(MaxConcurrencyPerEnvironment));
        await gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var uri = Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var abs) ? abs : new Uri(env.BaseUri, relativeOrAbsolute);
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                var token = await _tokens.GetTokenAsync(env.TenantId, [env.Scope], ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Headers.Add("OData-MaxVersion", "4.0");
                req.Headers.Add("OData-Version", "4.0");
                var prefer = "odata.include-annotations=\"*\"";
                if (pageSize is not null) prefer += $",odata.maxpagesize={pageSize}";
                req.Headers.Add("Prefer", prefer);

                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * (attempt + 1));
                    _log.LogWarning("Throttled by {Env}; retrying after {Wait}", env.Name, wait);
                    await Task.Delay(wait, ct);
                    continue;
                }
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new DataverseException((int)resp.StatusCode, ExtractError(body), uri.PathAndQuery);
                return JsonDocument.Parse(body);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Turns a Web API row into a flat dictionary, keeping formatted values as "<c>col@label</c>".</summary>
    internal static Dictionary<string, object?> Flatten(JsonElement item)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in item.EnumerateObject())
        {
            if (p.Name.StartsWith("@odata", StringComparison.Ordinal)) continue;
            if (p.Name.EndsWith(FormattedValue, StringComparison.Ordinal))
            {
                row[p.Name[..^FormattedValue.Length] + "@label"] = p.Value.GetString();
                continue;
            }
            if (p.Name.Contains('@')) continue;
            row[p.Name] = ToClr(p.Value);
        }
        return row;
    }

    internal static object? ToClr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? (object)l : v.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.Array => v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object ? (object?)Flatten(e) : ToClr(e)).ToList(),
        _ => Flatten(v),
    };

    private static string ExtractError(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            return d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch { return body.Length > 500 ? body[..500] : body; }
    }

    /// <summary>Last line of defence: nothing but GET leaves this process.</summary>
    internal sealed class ReadOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get)
                throw new InvalidOperationException($"Blocked {request.Method} request: Dataverse QnA is read-only.");
            return base.SendAsync(request, ct);
        }
    }
}

public sealed class DataverseException(int status, string message, string path)
    : Exception($"Dataverse returned {status}: {message}")
{
    public int Status { get; } = status;
    public string Path { get; } = path;
}
