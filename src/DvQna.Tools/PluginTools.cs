using System.Text.Json.Nodes;
using DvQna.Analysis;
using DvQna.Core;

namespace DvQna.Tools;

public sealed class GetMessagePipelineTool : Tool
{
    public override string Name => "get_message_pipeline";
    public override string Description =>
        "Everything that runs when a message (Create, Update, Delete, Retrieve, RetrieveMultiple, Assign…) happens on a table, in execution order: " +
        "plug-in steps (stage, mode, rank, filtering attributes, state), real-time and background classic workflows, business rules and " +
        "Dataverse-triggered cloud flows, with anti-pattern flags. Use for slow save/create/delete investigations.";
    public override JsonObject Schema => S.Obj(S.Env(), ("table", S.Str("Table logical name, e.g. account."), true),
        ("message", S.Str("SDK message name, e.g. Update."), true));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var table = args.Required("table").ToLowerInvariant();
        var message = args.Required("message");
        var warnings = new List<string>();
        var items = new List<PipelineItem>();

        var msg = (await ctx.Dataverse.QueryAsync(env, "sdkmessages", $"$select=sdkmessageid,name&$filter=name eq '{Results.Esc(message)}'", 1, ct)).Rows.FirstOrDefault()
            ?? throw new ToolInputException($"No SDK message named '{message}'.");
        message = msg.S("name")!;
        var stepQ = $"$select=name,stage,mode,rank,filteringattributes,statecode,asyncautodelete,customizationlevel,ismanaged" +
                    $"&$expand=sdkmessagefilterid($select=primaryobjecttypecode),plugintypeid($select=typename,assemblyname)" +
                    $"&$filter=_sdkmessageid_value eq {msg.S("sdkmessageid")} and ishidden/Value eq false and (sdkmessagefilterid/primaryobjecttypecode eq '{table}' or _sdkmessagefilterid_value eq null)";
        var steps = await ctx.Dataverse.QueryAsync(env, "sdkmessageprocessingsteps", stepQ, 2000, ct);
        foreach (var s in steps.Rows)
        {
            var pt = s.TryGetValue("plugintypeid", out var p) ? p as Dictionary<string, object?> : null;
            var filt = s.TryGetValue("sdkmessagefilterid", out var f) ? f as Dictionary<string, object?> : null;
            items.Add(new PipelineItem
            {
                Kind = "PluginStep",
                Name = s.S("name") ?? "",
                Handler = pt?.S("typename"),
                Stage = (int)(s.Num("stage") ?? 0),
                Mode = s.S("mode") == "1" ? "Async" : "Sync",
                Rank = (int)(s.Num("rank") ?? 0),
                FilteringAttributes = s.S("filteringattributes"),
                Enabled = s.S("statecode") == "0",
                AsyncAutoDelete = s.TryGetValue("asyncautodelete", out var ad) ? ad as bool? : null,
                Solution = pt?.S("assemblyname"),
                AnyEntity = filt is null,
            });
        }
        try
        {
            var wfFilter = $"type eq 1 and statecode eq 1 and primaryentity eq '{table}' and (category eq 0" + (message is "Create" or "Update" ? " or category eq 2)" : ")");
            var wfs = await ctx.Dataverse.QueryAsync(env, "workflows", $"$select=name,mode,category,triggeroncreate,triggerondelete,triggeronupdateattributelist,scope&$filter={wfFilter}", 500, ct);
            foreach (var w in wfs.Rows)
            {
                var isRule = w.S("category") == "2";
                if (!isRule)
                {
                    var fires = message.ToLowerInvariant() switch
                    {
                        "create" => w.S("triggeroncreate") == "True",
                        "delete" => w.S("triggerondelete") == "True",
                        "update" => !string.IsNullOrEmpty(w.S("triggeronupdateattributelist")),
                        _ => false,
                    };
                    if (!fires) continue;
                }
                items.Add(new PipelineItem
                {
                    Kind = isRule ? "BusinessRule" : "Workflow",
                    Name = w.S("name") ?? "",
                    Stage = isRule ? 20 : 40,
                    Mode = isRule || w.S("mode") == "1" ? "Sync" : "Async",
                    FilteringAttributes = w.S("triggeronupdateattributelist"),
                });
            }
        }
        catch (Exception ex) { warnings.Add("Could not read classic workflows: " + ex.Message); }

        try
        {
            var cbs = await ctx.Dataverse.QueryAsync(env, "callbackregistrations", $"$select=name,entityname,message,filteringattributes,scope&$filter=entityname eq '{table}'", 500, ct);
            foreach (var c in cbs.Rows.Where(c => c.Label("message").Contains(message, StringComparison.OrdinalIgnoreCase)))
                items.Add(new PipelineItem
                {
                    Kind = "FlowTrigger", Name = c.S("name") ?? "(flow trigger)", Stage = 40, Mode = "Async",
                    FilteringAttributes = c.S("filteringattributes"),
                });
        }
        catch (Exception ex) { warnings.Add("Could not read Dataverse-triggered flows (callbackregistration): " + ex.Message); }

        var ordered = PipelineBuilder.Build(items, message);
        var rows = ordered.Select((i, n) => new Dictionary<string, object?>
        {
            ["order"] = n + 1, ["kind"] = i.Kind, ["name"] = i.Name, ["handler"] = i.Handler, ["stage"] = PipelineBuilder.StageName(i.Stage),
            ["mode"] = i.Mode, ["rank"] = i.Rank, ["filtering_attributes"] = i.FilteringAttributes, ["enabled"] = i.Enabled,
            ["any_entity"] = i.AnyEntity ? true : null, ["assembly"] = i.Solution, ["flags"] = i.Flags.Count == 0 ? null : string.Join("; ", i.Flags),
        }).ToList();
        var enabled = ordered.Where(i => i.Enabled).ToList();
        var summary = $"{message} on {table}: {enabled.Count(i => i.Kind == "PluginStep" && i.Mode == "Sync")} sync and {enabled.Count(i => i.Kind == "PluginStep" && i.Mode == "Async")} async plug-in steps, " +
                      $"{enabled.Count(i => i.Kind == "Workflow")} classic workflow(s), {enabled.Count(i => i.Kind == "BusinessRule")} business rule(s), {enabled.Count(i => i.Kind == "FlowTrigger")} flow trigger(s). " +
                      $"{ordered.Count(i => i.Flags.Any(f => f.StartsWith("PLG") || f.StartsWith("WF")))} item(s) flagged.";
        return Results.Table(summary, rows, Results.Src(env, "sdkmessageprocessingstep, workflow, callbackregistration", stepQ), warnings, topN: 60, uiTitle: $"Pipeline: {message} {table}");
    }
}

public sealed class GetPluginPerformanceTool : Tool
{
    public override string Name => "get_plugin_performance";
    public override string Description =>
        "Plug-in execution statistics from plug-in trace logs (max 24h retention): executions, p50/p95/max duration (ms), errors and max depth, grouped by plug-in type, message, table and mode. " +
        "Only covers successful runs when the trace setting is 'All'.";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(24, 24), ("table", S.Str("Optional table logical name filter."), false),
        ("message", S.Str("Optional message filter, e.g. Update."), false), ("plugin", S.Str("Optional plug-in type name substring."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 24, 1, 24);
        var profile = await ctx.ProfileCache.GetAsync(env, false, ct);
        var filter = $"createdon ge {Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours))}";
        if (args.Str("table") is { } t) filter += $" and primaryentity eq '{Results.Esc(t)}'";
        if (args.Str("message") is { } m) filter += $" and messagename eq '{Results.Esc(m)}'";
        if (args.Str("plugin") is { } p) filter += $" and contains(typename,'{Results.Esc(p)}')";
        var q = $"$select=typename,messagename,primaryentity,mode,depth,performanceexecutionduration,exceptiondetails,createdon&$filter={filter}";
        var res = await ctx.Dataverse.QueryAsync(env, "plugintracelogs", q, 50000, ct);

        var rows = res.Rows.GroupBy(r => (Type: r.S("typename") ?? "", Msg: r.S("messagename") ?? "", Ent: r.S("primaryentity") ?? "", Mode: r.Label("mode")))
            .Select(g =>
            {
                var st = Stats.Summarize(g.Select(r => r.Num("performanceexecutionduration") ?? 0));
                var errors = g.Count(r => !string.IsNullOrWhiteSpace(r.S("exceptiondetails")));
                return new Dictionary<string, object?>
                {
                    ["plugin"] = g.Key.Type, ["message"] = g.Key.Msg, ["table"] = g.Key.Ent, ["mode"] = g.Key.Mode,
                    ["executions"] = st.Count, ["p50_ms"] = st.P50, ["p95_ms"] = st.P95, ["max_ms"] = st.Max, ["avg_ms"] = st.Avg,
                    ["errors"] = errors, ["error_rate"] = Math.Round(errors / (double)st.Count, 3), ["max_depth"] = g.Max(r => r.Num("depth")),
                };
            }).OrderByDescending(r => (double)r["p95_ms"]!).ToList();

        var warnings = new List<string>();
        if (profile.PluginTraceLogSetting is { } s && !s.Equals("All", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Trace log setting is '{s}', so these numbers cover only logged (failed) executions, not normal performance.");
        if (res.Rows.Count == 0) warnings.Add("No trace log rows matched. Trace logs are deleted after about 24 hours.");
        var slow = rows.Where(r => (string?)r["mode"] is "Synchronous" or "Sync" && (double)r["p95_ms"]! > 2000).ToList();
        var summary = res.Rows.Count == 0 ? $"No plug-in trace logs in the last {hours}h."
            : $"{res.Rows.Count} logged executions across {rows.Count} plug-in/message/table groups. Slowest p95: {rows[0]["plugin"]} ({rows[0]["message"]} {rows[0]["table"]}) {rows[0]["p95_ms"]} ms. "
              + $"{slow.Count} sync group(s) above the 2 s guidance.";
        return Results.Table(summary, rows, Results.Src(env, "plugintracelog", q, Results.Window(hours)), warnings, truncatedSource: res.Truncated, uiTitle: "Plug-in performance");
    }
}

public sealed class GetPluginFailuresTool : Tool
{
    public override string Name => "get_plugin_failures";
    public override string Description => "Plug-in exceptions from trace logs grouped by plug-in and the first line of the exception, with counts, latest time and a sample correlation id.";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(24, 24), ("plugin", S.Str("Optional plug-in type name substring."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 24, 1, 24);
        var filter = $"createdon ge {Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours))} and exceptiondetails ne null";
        if (args.Str("plugin") is { } p) filter += $" and contains(typename,'{Results.Esc(p)}')";
        var q = $"$select=typename,messagename,primaryentity,exceptiondetails,correlationid,createdon,depth&$filter={filter}&$orderby=createdon desc";
        var res = await ctx.Dataverse.QueryAsync(env, "plugintracelogs", q, 10000, ct);
        var rows = res.Rows.Where(r => !string.IsNullOrWhiteSpace(r.S("exceptiondetails")))
            .GroupBy(r => (Type: r.S("typename") ?? "", Error: Results.FirstLine(r.S("exceptiondetails"))))
            .Select(g => new Dictionary<string, object?>
            {
                ["plugin"] = g.Key.Type, ["error"] = g.Key.Error, ["count"] = g.Count(),
                ["messages"] = string.Join(", ", g.Select(r => $"{r.S("messagename")} {r.S("primaryentity")}").Distinct().Take(3)),
                ["latest"] = g.Max(r => r.S("createdon")), ["sample_correlation_id"] = g.First().S("correlationid"),
            }).OrderByDescending(r => (int)r["count"]!).ToList();
        var summary = rows.Count == 0 ? $"No plug-in exceptions in trace logs in the last {hours}h."
            : $"{res.Rows.Count} plug-in exception(s) in {rows.Count} group(s). Top: {rows[0]["plugin"]} ({rows[0]["count"]}×): {rows[0]["error"]}";
        return Results.Table(summary, rows, Results.Src(env, "plugintracelog", q, Results.Window(hours)), truncatedSource: res.Truncated, uiTitle: "Plug-in failures");
    }
}
