using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DvQna.Connectors;
using DvQna.Core;

namespace DvQna.Tools;

/// <summary>What an environment can answer (design section 7.4).</summary>
public sealed record CapabilityProfile
{
    public required string Env { get; init; }
    public string? Version { get; init; }
    public string? OrganizationName { get; init; }
    public string? CallerName { get; init; }
    public List<string> CallerRoles { get; init; } = [];
    public bool CallerIsSystemAdmin { get; init; }
    public string? PluginTraceLogSetting { get; init; }
    public bool? AuditEnabled { get; init; }
    public bool AppInsightsLinked { get; init; }
    public Dictionary<string, bool> ReadableTables { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class EnvironmentProfileCache(IDataverseReader dv)
{
    private readonly ConcurrentDictionary<string, CapabilityProfile> _cache = new();

    public async Task<CapabilityProfile> GetAsync(EnvironmentProfile env, bool refresh, CancellationToken ct)
    {
        if (!refresh && _cache.TryGetValue(env.Name, out var hit) && hit.BuiltAt > DateTimeOffset.UtcNow.AddMinutes(-15)) return hit;
        var p = await BuildAsync(env, ct);
        _cache[env.Name] = p;
        return p;
    }

    private async Task<CapabilityProfile> BuildAsync(EnvironmentProfile env, CancellationToken ct)
    {
        var notes = new List<string>();
        var who = await dv.FunctionAsync(env, "WhoAmI()", ct);
        var userId = who.GetProperty("UserId").GetString();
        string? version = null;
        try { version = (await dv.FunctionAsync(env, "RetrieveVersion()", ct)).GetProperty("Version").GetString(); } catch { }

        var org = (await dv.QueryAsync(env, "organizations", "$select=name,plugintracelogsetting,isauditenabled", 1, ct)).Rows.FirstOrDefault() ?? [];
        var user = await dv.GetAsync(env, $"systemusers({userId})?$select=fullname&$expand=systemuserroles_association($select=name)", ct);
        var roles = user.TryGetProperty("systemuserroles_association", out var r)
            ? r.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Distinct().OrderBy(x => x).ToList() : [];
        var isAdmin = roles.Contains("System Administrator");

        var readable = new Dictionary<string, bool>();
        foreach (var set in new[] { "plugintracelogs", "asyncoperations", "flowruns", "msdyn_solutionhistories", "callbackregistrations", "roleprivilegescollection" })
        {
            try { await dv.QueryAsync(env, set, "$top=1", 1, ct); readable[set] = true; }
            catch { readable[set] = false; }
        }

        var trace = org.Label("plugintracelogsetting");
        if (trace.Equals("Off", StringComparison.OrdinalIgnoreCase))
            notes.Add("Plug-in trace logging is Off: no plug-in durations or exceptions from trace logs.");
        else if (trace.StartsWith("Exception", StringComparison.OrdinalIgnoreCase))
            notes.Add("Plug-in trace logging is 'Exception': trace logs cover failed executions only, so success durations are unavailable.");
        if (!isAdmin) notes.Add("Caller is not a System Administrator: plug-in trace message text is hidden.");
        if (string.IsNullOrEmpty(env.AppInsightsResourceId)) notes.Add("No App Insights resource configured: no form-load, API or telemetry-based plug-in data.");
        foreach (var (t, ok) in readable.Where(k => !k.Value)) notes.Add($"Cannot read {t}.");

        return new CapabilityProfile
        {
            Env = env.Name,
            Version = version,
            OrganizationName = org.S("name"),
            CallerName = user.TryGetProperty("fullname", out var fn) ? fn.GetString() : null,
            CallerRoles = roles,
            CallerIsSystemAdmin = isAdmin,
            PluginTraceLogSetting = trace,
            AuditEnabled = org.TryGetValue("isauditenabled", out var a) ? a as bool? : null,
            AppInsightsLinked = !string.IsNullOrEmpty(env.AppInsightsResourceId),
            ReadableTables = readable,
            Notes = notes,
        };
    }
}

public sealed class ListEnvironmentsTool : Tool
{
    public override string Name => "list_environments";
    public override string Description => "List the Dataverse environments configured in this app (name, URL, tags).";
    public override JsonObject Schema => S.Obj();

    public override Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var rows = ctx.Profiles.All().Select(p => new Dictionary<string, object?>
        {
            ["name"] = p.Name, ["url"] = p.Url, ["tags"] = string.Join(",", p.Tags), ["app_insights"] = p.AppInsightsResourceId is not null,
        }).ToList();
        return Task.FromResult(new ToolResult
        {
            Summary = $"{rows.Count} environment(s) configured.", Data = rows, RowCount = rows.Count, FullData = rows,
            Sources = [new ToolSource("local", "profiles", null, null, DateTimeOffset.UtcNow)],
        });
    }
}

public sealed class GetEnvironmentProfileTool : Tool
{
    public override string Name => "get_environment_profile";
    public override string Description =>
        "Capability profile of an environment: version, caller and roles, plug-in trace setting, audit, which diagnostic tables are readable, " +
        "and notes on what cannot be answered. Call this first when unsure what data is available.";
    public override JsonObject Schema => S.Obj(S.Env(), ("refresh", new JsonObject { ["type"] = "boolean", ["description"] = "Rebuild instead of using the 15-minute cache." }, false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var refresh = args.Root.TryGetProperty("refresh", out var r) && r.ValueKind == JsonValueKind.True;
        var p = await ctx.ProfileCache.GetAsync(env, refresh, ct);
        var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(p, Json.Options), Json.Options)!;
        return new ToolResult
        {
            Summary = $"{env.Name}: v{p.Version}, caller {p.CallerName} ({(p.CallerIsSystemAdmin ? "System Administrator" : "not System Administrator")}), trace log setting {p.PluginTraceLogSetting}.",
            Data = [row], RowCount = 1, FullData = [row],
            Sources = [Results.Src(env, "WhoAmI, RetrieveVersion, organization, systemuser roles, table probes")],
            Warnings = p.Notes,
            Ui = new UiHint("kv", "Environment profile"),
        };
    }
}
