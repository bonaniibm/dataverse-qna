using System.Text.Json;
using System.Text.Json.Nodes;
using DvQna.Core;

namespace DvQna.Tools;

public sealed class CompareSolutionsTool : Tool
{
    public override string Name => "compare_solutions";
    public override string Description => "Compare installed (visible) solutions between two environments: missing on either side, version mismatches, managed vs unmanaged.";
    public override JsonObject Schema => S.Obj(("env_a", S.Str("First environment."), true), ("env_b", S.Str("Second environment."), true),
        ("include_microsoft", new JsonObject { ["type"] = "boolean", ["description"] = "Include Microsoft/first-party solutions (default false)." }, false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var a = ctx.Env(args.Required("env_a"));
        var b = ctx.Env(args.Required("env_b"));
        var includeMs = args.Root.TryGetProperty("include_microsoft", out var im) && im.ValueKind == JsonValueKind.True;
        const string q = "$select=uniquename,friendlyname,version,ismanaged,installedon&$expand=publisherid($select=uniquename)&$filter=isvisible eq true";
        var ra = ctx.Dataverse.QueryAsync(a, "solutions", q, 5000, ct);
        var rb = ctx.Dataverse.QueryAsync(b, "solutions", q, 5000, ct);
        bool Keep(Dictionary<string, object?> r) => includeMs || !((r.TryGetValue("publisherid", out var p) ? (p as Dictionary<string, object?>)?.S("uniquename") : null) is "microsoftcorporation" or "MicrosoftCorporation" or "dynamics365"
                                                                   || (r.S("uniquename") ?? "").StartsWith("msdyn", StringComparison.OrdinalIgnoreCase) || (r.S("uniquename") ?? "").StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));
        var da = (await ra).Rows.Where(Keep).ToDictionary(r => r.S("uniquename")!, StringComparer.OrdinalIgnoreCase);
        var db = (await rb).Rows.Where(Keep).ToDictionary(r => r.S("uniquename")!, StringComparer.OrdinalIgnoreCase);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var name in da.Keys.Union(db.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            da.TryGetValue(name, out var x);
            db.TryGetValue(name, out var y);
            var change = x is null ? $"only in {b.Name}" : y is null ? $"only in {a.Name}"
                : x.S("version") != y.S("version") ? "version differs"
                : x.S("ismanaged") != y.S("ismanaged") ? "managed state differs" : null;
            if (change is null) continue;
            rows.Add(new()
            {
                ["solution"] = name, ["display_name"] = (x ?? y)!.S("friendlyname"), ["change"] = change,
                [$"{a.Name}_version"] = x?.S("version"), [$"{b.Name}_version"] = y?.S("version"),
                [$"{a.Name}_managed"] = x?.S("ismanaged"), [$"{b.Name}_managed"] = y?.S("ismanaged"),
            });
        }
        var same = da.Keys.Intersect(db.Keys, StringComparer.OrdinalIgnoreCase).Count() - rows.Count(r => (string)r["change"]! is "version differs" or "managed state differs");
        return new ToolResult
        {
            Summary = $"{rows.Count} solution difference(s) between {a.Name} ({da.Count}) and {b.Name} ({db.Count}); {same} identical." + (includeMs ? "" : " Microsoft solutions excluded."),
            Data = rows.Take(50).ToList(), RowCount = rows.Count, Truncated = rows.Count > 50, FullData = rows,
            Sources = [Results.Src(a, "solution", q), Results.Src(b, "solution", q)], Ui = new UiHint("table", "Solution differences"),
        };
    }
}

public sealed class GetRecentChangesTool : Tool
{
    public override string Name => "get_recent_changes";
    public override string Description =>
        "Change timeline for an environment: solution imports/upgrades/uninstalls (solution history), plug-in assembly updates, plug-in step changes, " +
        "classic workflow and cloud flow changes, form and web resource changes. Use to correlate a slowdown or error with a deployment.";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(72, 720));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 72, 1, 720);
        var since = Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours));
        var rows = new List<Dictionary<string, object?>>();
        var warnings = new List<string>();

        async Task Add(string kind, string set, string query, Func<Dictionary<string, object?>, (string? When, string What, string? Detail, string? By)> map)
        {
            try
            {
                foreach (var r in (await ctx.Dataverse.QueryAsync(env, set, query, 500, ct)).Rows)
                {
                    var (when, what, detail, by) = map(r);
                    rows.Add(new() { ["when"] = when, ["kind"] = kind, ["what"] = what, ["detail"] = detail, ["by"] = by });
                }
            }
            catch (Exception ex) { warnings.Add($"{kind}: {ex.Message}"); }
        }

        await Add("Solution operation", "msdyn_solutionhistories",
            $"$select=msdyn_name,msdyn_solutionversion,msdyn_operation,msdyn_suboperation,msdyn_status,msdyn_starttime,msdyn_endtime,msdyn_ismanaged,msdyn_exceptionmessage&$filter=msdyn_starttime ge {since}&$orderby=msdyn_starttime desc",
            r => (r.S("msdyn_starttime"), $"{r.Label("msdyn_operation")} {r.S("msdyn_name")} {r.S("msdyn_solutionversion")}",
                  $"{r.Label("msdyn_suboperation")} · {r.Label("msdyn_status")}" + (string.IsNullOrEmpty(r.S("msdyn_exceptionmessage")) ? "" : " · " + Results.FirstLine(r.S("msdyn_exceptionmessage"))), null));
        await Add("Plug-in assembly", "pluginassemblies", $"$select=name,version,modifiedon,_modifiedby_value&$filter=modifiedon ge {since} and ishidden/Value eq false",
            r => (r.S("modifiedon"), $"{r.S("name")} {r.S("version")}", null, r.Label("_modifiedby_value")));
        await Add("Plug-in step", "sdkmessageprocessingsteps", $"$select=name,statecode,modifiedon,_modifiedby_value&$filter=modifiedon ge {since} and customizationlevel eq 1",
            r => (r.S("modifiedon"), r.S("name") ?? "", r.Label("statecode"), r.Label("_modifiedby_value")));
        await Add("Process/flow", "workflows", $"$select=name,category,statecode,modifiedon,_modifiedby_value&$filter=modifiedon ge {since} and type eq 1",
            r => (r.S("modifiedon"), r.S("name") ?? "", $"{r.Label("category")} · {r.Label("statecode")}", r.Label("_modifiedby_value")));
        await Add("Form", "systemforms", $"$select=name,objecttypecode,modifiedon,_modifiedby_value&$filter=modifiedon ge {since}",
            r => (r.S("modifiedon"), $"{r.S("objecttypecode")}: {r.S("name")}", null, r.Label("_modifiedby_value")));
        await Add("Web resource", "webresourceset", $"$select=name,modifiedon,_modifiedby_value&$filter=modifiedon ge {since} and ishidden/Value eq false",
            r => (r.S("modifiedon"), r.S("name") ?? "", null, r.Label("_modifiedby_value")));

        rows = rows.OrderByDescending(r => r.S("when")).ToList();
        var byKind = string.Join(", ", rows.GroupBy(r => r["kind"]).Select(g => $"{g.Count()} {g.Key}"));
        return Results.Table(rows.Count == 0 ? $"No tracked changes in the last {hours}h." : $"{rows.Count} change(s) in the last {hours}h: {byKind}. Latest: {rows[0]["when"]} {rows[0]["kind"]} {rows[0]["what"]}.",
            rows, Results.Src(env, "msdyn_solutionhistory, pluginassembly, sdkmessageprocessingstep, workflow, systemform, webresource", null, Results.Window(hours)),
            warnings, topN: 40, uiTitle: "Recent changes");
    }
}
