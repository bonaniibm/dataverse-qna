using System.Text.Json;
using System.Text.Json.Nodes;
using DvQna.Core;

namespace DvQna.Tools;

public sealed class RunHealthChecksTool : Tool
{
    public override string Name => "run_health_checks";
    public override string Description =>
        "Run the health-check rule library on an environment (plug-in step anti-patterns, real-time workflows, async backlog and suspended jobs, " +
        "trace-log setting, System Administrator count, environment variables without values). Returns findings with severity, evidence and fix.";
    public override JsonObject Schema => S.Obj(S.Env());

    private sealed record Finding(string Id, string Severity, string Title, string Evidence, string Fix);

    private static readonly string[] FirstPartyPrefixes =
        ["Microsoft.", "Microsoft ", "MetadataAudit", "ObjectModel", "ActivityFeeds", "FieldService.", "Msdyn.", "msdyn_", "Mscrm.", "PowerApps.", "Dynamics.", "ScheduleCommon.", "Resource Scheduling"];

    internal static bool IsFirstParty(string handler) =>
        FirstPartyPrefixes.Any(p => handler.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var findings = new List<Finding>();
        var warnings = new List<string>();
        var dv = ctx.Dataverse;

        async Task Check(string name, Func<Task> body)
        {
            try { await body(); } catch (Exception ex) { warnings.Add($"{name} skipped: {ex.Message}"); }
        }

        await Check("Plug-in steps", async () =>
        {
            var steps = (await dv.QueryAsync(env, "sdkmessageprocessingsteps",
                "$select=name,stage,mode,filteringattributes,statecode,asyncautodelete&$expand=sdkmessageid($select=name),sdkmessagefilterid($select=primaryobjecttypecode),plugintypeid($select=typename)&$filter=customizationlevel eq 1 and ishidden/Value eq false",
                5000, ct)).Rows;
            // Microsoft first-party handlers cannot be changed by admins, so they are not findings.
            static string Handler(Dictionary<string, object?> s) => (s.TryGetValue("plugintypeid", out var p) ? (p as Dictionary<string, object?>)?.S("typename") : null) ?? s.S("name") ?? "";
            var firstParty = steps.Count(s => IsFirstParty(Handler(s)) || IsFirstParty(s.S("name") ?? ""));
            steps = steps.Where(s => !IsFirstParty(Handler(s)) && !IsFirstParty(s.S("name") ?? "")).ToList();
            if (firstParty > 0) warnings.Add($"{firstParty} Microsoft first-party plug-in step(s) excluded from plug-in findings.");
            string Msg(Dictionary<string, object?> s) => (s.TryGetValue("sdkmessageid", out var m) ? (m as Dictionary<string, object?>)?.S("name") : null) ?? "";
            string Ent(Dictionary<string, object?> s) => (s.TryGetValue("sdkmessagefilterid", out var f) ? (f as Dictionary<string, object?>)?.S("primaryobjecttypecode") : null) ?? "any";
            var active = steps.Where(s => s.S("statecode") == "0").ToList();
            var noFilter = active.Where(s => Msg(s) == "Update" && s.S("mode") == "0" && string.IsNullOrWhiteSpace(s.S("filteringattributes"))).ToList();
            if (noFilter.Count > 0)
                findings.Add(new("PLG-001", "High", "Sync Update steps without filtering attributes",
                    $"{noFilter.Count}: " + string.Join("; ", noFilter.Take(8).Select(s => $"{s.S("name")} ({Ent(s)})")), "Set filtering attributes so the step runs only when relevant columns change."));
            var onRetrieve = active.Where(s => Msg(s) is "Retrieve" or "RetrieveMultiple" && s.S("mode") == "0").ToList();
            if (onRetrieve.Count > 0)
                findings.Add(new("PLG-002", "High", "Sync steps on Retrieve/RetrieveMultiple",
                    $"{onRetrieve.Count}: " + string.Join("; ", onRetrieve.Take(8).Select(s => $"{s.S("name")} ({Msg(s)} {Ent(s)})")), "These run on every read: keep them minimal or remove them."));
            var noAutoDelete = active.Where(s => s.S("mode") == "1" && s.S("asyncautodelete") == "False").ToList();
            if (noAutoDelete.Count > 0)
                findings.Add(new("PLG-003", "Medium", "Async steps without auto-delete of successful jobs",
                    $"{noAutoDelete.Count}: " + string.Join("; ", noAutoDelete.Take(8).Select(s => s.S("name"))), "Enable 'Delete AsyncOperation if StatusCode = Successful' to limit asyncoperation growth."));
            var perStage = active.Where(s => s.S("mode") == "0").GroupBy(s => (Msg(s), Ent(s), s.S("stage"))).Where(g => g.Count() > 5).ToList();
            foreach (var g in perStage)
                findings.Add(new("PLG-004", "Medium", "Many sync steps on one table/message/stage", $"{g.Count()} sync steps on {g.Key.Item1} {g.Key.Item2} stage {g.Key.Item3}", "Consolidate logic or move non-critical work to async."));
            var disabled = steps.Count(s => s.S("statecode") == "1");
            if (disabled > 0) findings.Add(new("PLG-009", "Low", "Disabled custom plug-in steps", $"{disabled} disabled step(s)", "Remove obsolete registrations to keep the pipeline clear."));
        });

        await Check("Workflows", async () =>
        {
            var wfs = (await dv.QueryAsync(env, "workflows", "$select=name,mode,primaryentity&$filter=type eq 1 and statecode eq 1 and category eq 0", 5000, ct)).Rows;
            var realtime = wfs.Where(w => w.S("mode") == "1").ToList();
            if (realtime.Count > 0)
                findings.Add(new("WF-001", "Medium", "Active real-time (synchronous) classic workflows",
                    $"{realtime.Count}: " + string.Join("; ", realtime.Take(8).Select(w => $"{w.S("name")} ({w.S("primaryentity")})")), "They run inside the user's transaction; consider plug-ins or async processing."));
            var background = wfs.Count - realtime.Count;
            if (background > 0) findings.Add(new("WF-002", "Info", "Active background classic workflows", $"{background} workflow(s)", "Plan migration to cloud flows."));
        });

        await Check("Async queue", async () =>
        {
            var open = await dv.QueryAsync(env, "asyncoperations", "$select=statecode,statuscode,createdon&$filter=statecode ne 3&$orderby=createdon asc", 20000, ct);
            var waiting = open.Rows.Where(r => r.S("statuscode") == "0").ToList();
            var oldest = waiting.FirstOrDefault()?.Date("createdon");
            if (waiting.Count > 500 || (oldest is { } o && DateTimeOffset.UtcNow - o > TimeSpan.FromMinutes(30)))
                findings.Add(new("JOB-001", "High", "Async backlog", $"{waiting.Count}{(open.Truncated ? "+" : "")} jobs waiting for resources; oldest queued {(oldest is null ? "?" : Math.Round((DateTimeOffset.UtcNow - oldest.Value).TotalMinutes) + " min")} ago",
                    "Find what is flooding the queue with get_async_backlog."));
            var suspended = open.Rows.Count(r => r.S("statecode") == "1");
            if (suspended > 0) findings.Add(new("JOB-002", "Medium", "Suspended or waiting jobs", $"{suspended}{(open.Truncated ? "+" : "")} job(s) in Suspended state", "Review: resume or cancel, and fix the cause."));
        });

        await Check("Organization settings", async () =>
        {
            var p = await ctx.ProfileCache.GetAsync(env, false, ct);
            if (string.Equals(p.PluginTraceLogSetting, "All", StringComparison.OrdinalIgnoreCase))
                findings.Add(new("CFG-001", env.IsProd ? "Medium" : "Info", "Plug-in trace logging set to All",
                    $"plugintracelogsetting = All{(env.IsProd ? " in a PROD-tagged environment" : "")}", "Set it back to Exception or Off after debugging; it uses storage and Microsoft may disable it above 100 GB."));
        });

        await Check("Environment variables", async () =>
        {
            var defs = (await dv.QueryAsync(env, "environmentvariabledefinitions",
                "$select=schemaname,defaultvalue&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)", 5000, ct)).Rows;
            var empty = defs.Where(d => string.IsNullOrEmpty(d.S("defaultvalue")) &&
                (d.TryGetValue("environmentvariabledefinition_environmentvariablevalue", out var v) ? (v as List<object?>)?.Count ?? 0 : 0) == 0).ToList();
            if (empty.Count > 0)
                findings.Add(new("CFG-003", "Medium", "Environment variables without a value", $"{empty.Count}: " + string.Join(", ", empty.Take(10).Select(d => d.S("schemaname"))), "Set a current value or default value."));
        });

        await Check("System administrators", async () =>
        {
            var admins = (await dv.QueryAsync(env, "systemusers",
                "$select=fullname,accessmode&$filter=isdisabled eq false and systemuserroles_association/any(r: r/name eq 'System Administrator')", 1000, ct)).Rows;
            var humans = admins.Where(a => a.S("accessmode") != "4").ToList();
            findings.Add(new("SEC-002", humans.Count > 10 ? "Medium" : "Info", "Enabled users with System Administrator",
                $"{humans.Count} human user(s), {admins.Count - humans.Count} application/non-interactive user(s)", "Review for least privilege."));
        });

        var order = new Dictionary<string, int> { ["High"] = 0, ["Medium"] = 1, ["Low"] = 2, ["Info"] = 3 };
        var rows = findings.OrderBy(f => order.GetValueOrDefault(f.Severity, 9)).ThenBy(f => f.Id)
            .Select(f => new Dictionary<string, object?> { ["id"] = f.Id, ["severity"] = f.Severity, ["title"] = f.Title, ["evidence"] = f.Evidence, ["fix"] = f.Fix }).ToList();
        var counts = string.Join(", ", findings.GroupBy(f => f.Severity).OrderBy(g => order.GetValueOrDefault(g.Key, 9)).Select(g => $"{g.Count()} {g.Key}"));
        return Results.Table($"Health check on {env.Name}: {findings.Count} finding(s) ({(counts == "" ? "none" : counts)}).", rows,
            Results.Src(env, "sdkmessageprocessingstep, workflow, asyncoperation, organization, environmentvariabledefinition, systemuser"), warnings, topN: 40, uiTitle: "Health check");
    }
}

public sealed class GetTableStatsTool : Tool
{
    public override string Name => "get_table_stats";
    public override string Description => "Approximate row counts (RetrieveTotalRecordCount snapshot, not real-time) for tables. Defaults to system tables that commonly grow: asyncoperation, plugintracelog, workflowlog, bulkdeletefailure, principalobjectaccess, plus account and contact.";
    public override JsonObject Schema => S.Obj(S.Env(), ("tables", S.StrList("Table logical names."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var tables = args.List("tables");
        if (tables.Count == 0) tables = ["asyncoperation", "plugintracelog", "workflowlog", "bulkdeletefailure", "principalobjectaccess", "account", "contact", "activitypointer", "annotation"];
        var call = $"RetrieveTotalRecordCount(EntityNames=[{string.Join(",", tables.Select(t => $"'{Results.Esc(t.ToLowerInvariant())}'"))}])";
        var res = await ctx.Dataverse.FunctionAsync(env, call, ct);
        var coll = res.GetProperty("EntityRecordCountCollection");
        var keys = coll.GetProperty("Keys").EnumerateArray().Select(k => k.GetString()!).ToList();
        var values = coll.GetProperty("Values").EnumerateArray().Select(v => v.GetInt64()).ToList();
        var rows = keys.Zip(values).Select(kv => new Dictionary<string, object?> { ["table"] = kv.First, ["rows"] = kv.Second })
            .OrderByDescending(r => (long)r["rows"]!).ToList();
        return Results.Table($"Row counts for {rows.Count} table(s); largest: {rows.FirstOrDefault()?["table"]} ({rows.FirstOrDefault()?["rows"]:N0}).", rows,
            Results.Src(env, "RetrieveTotalRecordCount", call), ["Counts are a platform snapshot, refreshed periodically, not live."], uiTitle: "Table row counts");
    }
}
