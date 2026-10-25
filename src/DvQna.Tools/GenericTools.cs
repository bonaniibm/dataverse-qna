using System.Text.Json;
using System.Text.Json.Nodes;
using DvQna.Core;

namespace DvQna.Tools;

public sealed class DataverseReadTool : Tool
{
    public override string Name => "dataverse_read";
    public override string Description =>
        "Escape hatch: run a read-only OData query or FetchXML against any table when no purpose-built tool fits. " +
        "Always select only needed columns and filter tightly. Max 500 rows. Option set labels come back as '<column>@label'.";
    public override JsonObject Schema => S.Obj(S.Env(),
        ("entity_set", S.Str("Entity set name (plural), e.g. accounts, sdkmessageprocessingsteps."), true),
        ("odata_query", S.Str("OData query options without '?', e.g. $select=name&$filter=statecode eq 0&$top=50. Must include $select."), false),
        ("fetchxml", S.Str("FetchXML query (alternative to odata_query)."), false),
        ("max_rows", S.Int("Row cap (default 100, max 500)."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var set = args.Required("entity_set");
        var max = args.Int("max_rows", 100, 1, 500);
        var fetch = args.Str("fetchxml");
        var odata = args.Str("odata_query");
        if (fetch is null && odata is null) throw new ToolInputException("Provide odata_query or fetchxml.");
        if (fetch is null && !odata!.Contains("$select", StringComparison.OrdinalIgnoreCase) && !odata.Contains("$apply", StringComparison.OrdinalIgnoreCase))
            throw new ToolInputException("odata_query must include $select to limit columns.");
        var res = fetch is not null
            ? await ctx.Dataverse.FetchXmlAsync(env, set, fetch, max, ct)
            : await ctx.Dataverse.QueryAsync(env, set, odata!, max, ct);
        return Results.Table($"{res.Rows.Count} row(s) from {set}{(res.Truncated ? $" (capped at {max})" : "")}.", res.Rows,
            Results.Src(env, set, fetch ?? odata), topN: 50, truncatedSource: res.Truncated, uiTitle: set);
    }
}

public sealed class DescribeTableTool : Tool
{
    public override string Name => "describe_table";
    public override string Description => "Table metadata: entity set name, primary columns, ownership, and the list of columns with types. Use before writing dataverse_read queries on unfamiliar tables.";
    public override JsonObject Schema => S.Obj(S.Env(), ("table", S.Str("Table logical name."), true), ("custom_only", new JsonObject { ["type"] = "boolean", ["description"] = "Only custom columns." }, false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var table = args.Required("table").ToLowerInvariant();
        var customOnly = args.Root.TryGetProperty("custom_only", out var c) && c.ValueKind == JsonValueKind.True;
        var path = $"EntityDefinitions(LogicalName='{Results.Esc(table)}')?$select=LogicalName,EntitySetName,PrimaryIdAttribute,PrimaryNameAttribute,OwnershipType,IsCustomEntity,TableType,ObjectTypeCode" +
                   "&$expand=Attributes($select=LogicalName,AttributeType,IsCustomAttribute,IsValidForRead)";
        var md = await ctx.Dataverse.GetAsync(env, path, ct);
        var rows = md.GetProperty("Attributes").EnumerateArray()
            .Where(a => !customOnly || a.GetProperty("IsCustomAttribute").GetBoolean())
            .Select(a => new Dictionary<string, object?>
            {
                ["column"] = a.GetProperty("LogicalName").GetString(), ["type"] = a.GetProperty("AttributeType").GetString(),
                ["custom"] = a.GetProperty("IsCustomAttribute").GetBoolean(),
            }).OrderBy(r => (string)r["column"]!).ToList();
        string P(string n) => md.TryGetProperty(n, out var v) ? v.ToString() : "";
        return Results.Table($"{table}: entity set '{P("EntitySetName")}', id '{P("PrimaryIdAttribute")}', name '{P("PrimaryNameAttribute")}', ownership {P("OwnershipType")}, type {P("TableType")}, {rows.Count} column(s).",
            rows, Results.Src(env, "EntityDefinitions", path), topN: 150, uiTitle: $"Columns: {table}");
    }
}

public sealed class QueryTelemetryTool : Tool
{
    public override string Name => "query_telemetry";
    public override string Description =>
        "Run a read-only KQL query on the App Insights resource linked to the environment (Dataverse export: requests, dependencies with type 'Plugin' / 'SDK ...' / 'UCI REQUEST', exceptions, pageViews). " +
        "Only works when the environment profile has App Insights configured. Keep queries aggregated (summarize/top).";
    public override JsonObject Schema => S.Obj(S.Env(), ("kql", S.Str("KQL query."), true), S.Hours(24, 168));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var kql = args.Required("kql");
        var hours = args.Int("window_hours", 24, 1, 168);
        var rows = await ctx.AppInsights.QueryAsync(env, kql, TimeSpan.FromHours(hours), 5000, ct);
        return Results.Table($"{rows.Count} row(s) from App Insights.", rows, Results.Src(env, "appinsights", kql, Results.Window(hours)), uiTitle: "Telemetry");
    }
}

public sealed class ToolRegistry
{
    public IReadOnlyList<Tool> Tools { get; } =
    [
        new ListEnvironmentsTool(), new GetEnvironmentProfileTool(),
        new GetRunningJobsTool(), new GetAsyncBacklogTool(), new GetJobFailuresTool(), new GetFlowRunsTool(),
        new GetMessagePipelineTool(), new GetPluginPerformanceTool(), new GetPluginFailuresTool(),
        new CompareRolesTool(), new GetRolePrivilegesTool(),
        new CompareSolutionsTool(), new GetRecentChangesTool(),
        new RunHealthChecksTool(), new GetTableStatsTool(),
        new DataverseReadTool(), new DescribeTableTool(), new QueryTelemetryTool(),
    ];

    public Tool? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
