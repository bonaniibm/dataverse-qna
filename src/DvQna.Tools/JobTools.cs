using System.Text.Json.Nodes;
using DvQna.Analysis;
using DvQna.Core;

namespace DvQna.Tools;

public sealed class GetRunningJobsTool : Tool
{
    public override string Name => "get_running_jobs";
    public override string Description => "System jobs currently in progress, pausing or cancelling (asyncoperation statecode Locked), oldest first, with how long each has been running.";
    public override JsonObject Schema => S.Obj(S.Env());

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        const string q = "$select=name,operationtype,statuscode,startedon,createdon,depth,_ownerid_value,_regardingobjectid_value&$filter=statecode eq 2&$orderby=startedon asc";
        var res = await ctx.Dataverse.QueryAsync(env, "asyncoperations", q, 1000, ct);
        var now = DateTimeOffset.UtcNow;
        var rows = res.Rows.Select(r => new Dictionary<string, object?>
        {
            ["name"] = r.S("name"),
            ["type"] = r.Label("operationtype"),
            ["status"] = r.Label("statuscode"),
            ["started"] = r.S("startedon"),
            ["running_minutes"] = r.Date("startedon") is { } s ? Math.Round((now - s).TotalMinutes, 1) : null,
            ["owner"] = r.Label("_ownerid_value"),
            ["regarding"] = r.Label("_regardingobjectid_value"),
            ["depth"] = r.S("depth"),
        }).ToList();
        var summary = rows.Count == 0 ? "No system jobs are running right now."
            : $"{rows.Count} job(s) running; longest has been running {rows.Max(r => (double?)r["running_minutes"] ?? 0):0.#} min. "
              + string.Join("; ", rows.GroupBy(r => r["type"]).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}: {g.Count()}"));
        return Results.Table(summary, rows, Results.Src(env, "asyncoperation", q), truncatedSource: res.Truncated, uiTitle: "Running jobs");
    }
}

public sealed class GetAsyncBacklogTool : Tool
{
    public override string Name => "get_async_backlog";
    public override string Description =>
        "Async queue health: open jobs (waiting, waiting for resources, suspended) grouped by type and name with the oldest queued job; " +
        "plus, for completed jobs in the window, throughput, queue-wait and run-time percentiles and failure counts.";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(24, 168));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 24, 1, 168);
        var since = Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours));
        const string openQ = "$select=name,operationtype,statecode,statuscode,createdon,postponeuntil&$filter=statecode ne 3&$orderby=createdon asc";
        var doneQ = $"$select=name,operationtype,statuscode,createdon,startedon,completedon&$filter=statecode eq 3 and completedon ge {since}";
        var open = await ctx.Dataverse.QueryAsync(env, "asyncoperations", openQ, 20000, ct);
        var done = await ctx.Dataverse.QueryAsync(env, "asyncoperations", doneQ, 20000, ct);
        var now = DateTimeOffset.UtcNow;

        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in open.Rows.GroupBy(r => (Type: r.Label("operationtype"), Name: r.S("name") ?? "", Status: r.Label("statuscode"))))
        {
            var oldest = g.Min(r => r.Date("createdon"));
            rows.Add(new()
            {
                ["section"] = "open", ["type"] = g.Key.Type, ["name"] = g.Key.Name, ["status"] = g.Key.Status, ["count"] = g.Count(),
                ["oldest_created"] = oldest?.ToString("u"), ["oldest_age_minutes"] = oldest is { } o ? Math.Round((now - o).TotalMinutes) : null,
            });
        }
        foreach (var g in done.Rows.GroupBy(r => (Type: r.Label("operationtype"), Name: r.S("name") ?? "")))
        {
            var wait = Stats.Summarize(g.Where(r => r.Date("startedon") is not null && r.Date("createdon") is not null)
                .Select(r => (r.Date("startedon")!.Value - r.Date("createdon")!.Value).TotalSeconds));
            var run = Stats.Summarize(g.Where(r => r.Date("completedon") is not null && r.Date("startedon") is not null)
                .Select(r => (r.Date("completedon")!.Value - r.Date("startedon")!.Value).TotalSeconds));
            rows.Add(new()
            {
                ["section"] = "completed", ["type"] = g.Key.Type, ["name"] = g.Key.Name, ["count"] = g.Count(),
                ["failed"] = g.Count(r => r.S("statuscode") == "31"), ["canceled"] = g.Count(r => r.S("statuscode") == "32"),
                ["wait_p50_s"] = wait.P50, ["wait_p95_s"] = wait.P95, ["run_p50_s"] = run.P50, ["run_p95_s"] = run.P95,
            });
        }
        rows = rows.OrderBy(r => (string)r["section"]! == "open" ? 0 : 1).ThenByDescending(r => (int)r["count"]!).ToList();

        var waitingRes = open.Rows.Count(r => r.S("statuscode") == "0");
        var suspended = open.Rows.Count(r => r.S("statecode") == "1");
        var oldestQueued = open.Rows.Where(r => r.S("statuscode") == "0").Min(r => r.Date("createdon"));
        var failed = done.Rows.Count(r => r.S("statuscode") == "31");
        var summary = $"Open: {open.Rows.Count} ({waitingRes} waiting for resources, {suspended} suspended/waiting"
            + (oldestQueued is { } oq ? $", oldest queued {Math.Round((now - oq).TotalMinutes)} min ago" : "") + "). "
            + $"Completed in last {hours}h: {done.Rows.Count} ({failed} failed), ~{done.Rows.Count / (double)hours:0.#}/h.";
        var r1 = Results.Table(summary, rows, Results.Src(env, "asyncoperation", openQ + " | " + doneQ, Results.Window(hours)),
            truncatedSource: open.Truncated || done.Truncated, uiTitle: "Async backlog");
        return r1;
    }
}

public sealed class GetJobFailuresTool : Tool
{
    public override string Name => "get_job_failures";
    public override string Description => "Failed system jobs (async plug-ins, workflows, bulk deletes…) in the window, grouped by job name and error, with counts, retry counts and the latest occurrence.";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(24, 168), ("name_contains", S.Str("Optional filter on the job name."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 24, 1, 168);
        var since = Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours));
        var filter = $"statuscode eq 31 and completedon ge {since}";
        if (args.Str("name_contains") is { } n) filter += $" and contains(name,'{Results.Esc(n)}')";
        var q = $"$select=name,operationtype,message,friendlymessage,errorcode,retrycount,completedon,asyncoperationid&$filter={filter}&$orderby=completedon desc";
        var res = await ctx.Dataverse.QueryAsync(env, "asyncoperations", q, 5000, ct);
        var rows = res.Rows.GroupBy(r => (Name: r.S("name") ?? "", Error: Results.FirstLine(r.S("friendlymessage") ?? r.S("message"))))
            .Select(g => new Dictionary<string, object?>
            {
                ["name"] = g.Key.Name, ["type"] = g.First().Label("operationtype"), ["error"] = g.Key.Error, ["error_code"] = g.First().S("errorcode"),
                ["count"] = g.Count(), ["max_retries"] = g.Max(r => r.Num("retrycount")), ["latest"] = g.Max(r => r.S("completedon")),
                ["sample_job_id"] = g.First().S("asyncoperationid"),
            })
            .OrderByDescending(r => (int)r["count"]!).ToList();
        var summary = res.Rows.Count == 0 ? $"No failed system jobs in the last {hours}h."
            : $"{res.Rows.Count} failed job(s) in the last {hours}h across {rows.Count} distinct name/error group(s). Top: {rows[0]["name"]} ({rows[0]["count"]}×): {rows[0]["error"]}";
        return Results.Table(summary, rows, Results.Src(env, "asyncoperation", q, Results.Window(hours)), truncatedSource: res.Truncated, uiTitle: "Failed jobs");
    }
}

public sealed class GetFlowRunsTool : Tool
{
    public override string Name => "get_flow_runs";
    public override string Description => "Cloud flow run history from the flowrun elastic table: runs, failures and durations per flow in the window (default retention is 28 days).";
    public override JsonObject Schema => S.Obj(S.Env(), S.Hours(24, 672), ("status", S.Str("Optional status filter, e.g. Failed."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var hours = args.Int("window_hours", 24, 1, 672);
        var since = Results.Iso(DateTimeOffset.UtcNow.AddHours(-hours));
        var q = $"$filter=createdon ge {since}&$orderby=createdon desc";
        var res = await ctx.Dataverse.QueryAsync(env, "flowruns", q, 20000, ct);
        var status = args.Str("status");
        var runs = res.Rows.Where(r => status is null || (r.Label("status")).Contains(status, StringComparison.OrdinalIgnoreCase)).ToList();
        string FlowName(Dictionary<string, object?> r) =>
            r.S("_workflow_value@label") ?? r.S("_workflowid_value@label") ?? r.S("workflowname") ?? r.S("_workflow_value") ?? r.S("workflowid") ?? r.S("name") ?? "(unknown)";
        var rows = runs.GroupBy(FlowName).Select(g =>
        {
            var d = Stats.Summarize(g.Select(r => r.Num("duration")).Where(x => x is not null).Select(x => x!.Value));
            return new Dictionary<string, object?>
            {
                ["flow"] = g.Key, ["runs"] = g.Count(),
                ["failed"] = g.Count(r => r.Label("status").Contains("Fail", StringComparison.OrdinalIgnoreCase)),
                ["duration_p50"] = d.P50, ["duration_p95"] = d.P95, ["latest"] = g.Max(r => r.S("createdon")),
            };
        }).OrderByDescending(r => (int)r["failed"]!).ThenByDescending(r => (int)r["runs"]!).ToList();
        var failed = rows.Sum(r => (int)r["failed"]!);
        return Results.Table($"{runs.Count} flow run(s) in the last {hours}h across {rows.Count} flow(s); {failed} failed.",
            rows, Results.Src(env, "flowrun", q, Results.Window(hours)), truncatedSource: res.Truncated, uiTitle: "Flow runs",
            warnings: ["flowrun is an elastic table: Web API only; duration units as stored by Power Automate."]);
    }
}
