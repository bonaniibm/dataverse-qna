using System.Text.Json.Nodes;
using DvQna.Analysis;
using DvQna.Connectors;
using DvQna.Core;

namespace DvQna.Tools;

internal static class RoleLoader
{
    /// <summary>Root business-unit roles plus their privileges, keyed by privilege name.</summary>
    public static async Task<RoleSnapshot> LoadAsync(IDataverseReader dv, EnvironmentProfile env, IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var roleRows = (await dv.QueryAsync(env, "roles", "$select=roleid,name,_roletemplateid_value,isinherited,ismanaged&$filter=_parentroleid_value eq null", 5000, ct)).Rows;
        var roles = roleRows.Select(r => new RoleInfo(
                Guid.Parse(r.S("roleid")!), r.S("name") ?? "",
                Guid.TryParse(r.S("_roletemplateid_value"), out var t) ? t : null,
                int.TryParse(r.S("isinherited"), out var inh) ? inh : null,
                r.S("ismanaged") == "True"))
            .Where(r => names.Count == 0 || names.Any(n => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var privNames = (await dv.QueryAsync(env, "privileges", "$select=privilegeid,name", 20000, ct)).Rows
            .ToDictionary(r => r.S("privilegeid")!, r => r.S("name")!, StringComparer.OrdinalIgnoreCase);
        var defined = new HashSet<string>(privNames.Values, StringComparer.OrdinalIgnoreCase);
        if (roles.Count == 0) return new RoleSnapshot(env.Name, roles, [], defined);

        var privileges = new List<RolePrivilege>();
        foreach (var chunk in roles.Chunk(15))
        {
            var filter = string.Join(" or ", chunk.Select(r => $"roleid eq {r.RoleId}"));
            var rows = (await dv.QueryAsync(env, "roleprivilegescollection", $"$select=roleid,privilegeid,privilegedepthmask&$filter={filter}", 200000, ct)).Rows;
            foreach (var r in rows)
                if (privNames.TryGetValue(r.S("privilegeid") ?? "", out var pn))
                    privileges.Add(new RolePrivilege(Guid.Parse(r.S("roleid")!), pn, (int)(r.Num("privilegedepthmask") ?? 0)));
        }
        return new RoleSnapshot(env.Name, roles, privileges, defined);
    }
}

public sealed class CompareRolesTool : Tool
{
    public override string Name => "compare_roles";
    public override string Description =>
        "Compare security roles between two environments deterministically: roles only in one side, privileges added/removed, access level (depth) differences " +
        "and role setting differences, ranked by risk. Roles are matched by role id, then template id, then name. Privileges are matched by name.";
    public override JsonObject Schema => S.Obj(("env_a", S.Str("First environment (usually the lower one, e.g. UAT)."), true),
        ("env_b", S.Str("Second environment (e.g. PROD)."), true),
        ("roles", S.StrList("Role names to compare. Empty = all root business-unit roles."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var a = ctx.Env(args.Required("env_a"));
        var b = ctx.Env(args.Required("env_b"));
        var names = args.List("roles");
        var sa = RoleLoader.LoadAsync(ctx.Dataverse, a, names, ct);
        var sb = RoleLoader.LoadAsync(ctx.Dataverse, b, names, ct);
        var diff = RoleDiff.Compare(await sa, await sb);

        var rows = new List<Dictionary<string, object?>>();
        foreach (var m in diff.Matches.Where(m => m.A is null || m.B is null))
            rows.Add(new() { ["role"] = m.A?.Name ?? m.B!.Name, ["kind"] = "role", ["change"] = m.MatchedBy == "only in A" ? $"only in {a.Name}" : $"only in {b.Name}", ["risk"] = 5 });
        foreach (var s in diff.Settings)
            rows.Add(new() { ["role"] = s.Role, ["kind"] = "setting", ["privilege"] = s.Setting, [$"{a.Name}"] = s.A, [$"{b.Name}"] = s.B, ["change"] = "differs", ["risk"] = 2 });
        foreach (var d in diff.Privileges)
            rows.Add(new()
            {
                ["role"] = d.Role, ["kind"] = "privilege", ["privilege"] = d.Privilege, ["table"] = d.Table, ["access"] = d.Access,
                [$"{a.Name}"] = d.DepthA, [$"{b.Name}"] = d.DepthB,
                ["change"] = d.Change.Replace(" in A", " in " + a.Name).Replace(" in B", " in " + b.Name), ["risk"] = d.Risk,
            });
        rows = rows.OrderByDescending(r => (int)r["risk"]!).ToList();

        var compared = diff.Matches.Count(m => m.A is not null && m.B is not null);
        var higherInB = diff.Privileges.Count(p => p.Change is "higher in B" or "only in B");
        var missingTables = diff.Privileges.Count(p => p.Change.StartsWith("table not in"));
        var real = diff.Privileges.Count - missingTables;
        var warnings = new List<string>();
        if (names.Count > 0)
            foreach (var n in names.Where(n => !diff.Matches.Any(m => (m.A?.Name ?? m.B?.Name)?.Equals(n, StringComparison.OrdinalIgnoreCase) == true)))
                warnings.Add($"Role '{n}' was not found in either environment.");
        var summary = $"Compared {compared} matched role(s) between {a.Name} and {b.Name}: {diff.Matches.Count(m => m.B is null)} only in {a.Name}, {diff.Matches.Count(m => m.A is null)} only in {b.Name}, " +
                      $"{real} permission difference(s) ({higherInB} grant more access in {b.Name}), {diff.Settings.Count} setting difference(s); " +
                      $"{missingTables} more are for tables that exist in only one environment (component difference, not a permission difference).";
        return new ToolResult
        {
            Summary = summary, Data = rows.Take(40).ToList(), RowCount = rows.Count, Truncated = rows.Count > 40, FullData = rows,
            Sources = [Results.Src(a, "role, roleprivileges, privilege"), Results.Src(b, "role, roleprivileges, privilege")],
            Warnings = warnings, Ui = new UiHint("table", $"Role diff {a.Name} vs {b.Name}"),
        };
    }
}

public sealed class GetRolePrivilegesTool : Tool
{
    public override string Name => "get_role_privileges";
    public override string Description => "Privilege matrix for one security role: one row per table with the access level for Create, Read, Write, Delete, Append, AppendTo, Assign, Share, plus miscellaneous privileges.";
    public override JsonObject Schema => S.Obj(S.Env(), ("role", S.Str("Role name."), true), ("table_contains", S.Str("Optional filter on table name."), false));

    public override async Task<ToolResult> RunAsync(ToolArgs args, ToolContext ctx, CancellationToken ct)
    {
        var env = ctx.Env(args.Str("env"));
        var role = args.Required("role");
        var snap = await RoleLoader.LoadAsync(ctx.Dataverse, env, [role], ct);
        if (snap.Roles.Count == 0) throw new ToolInputException($"Role '{role}' not found in {env.Name}.");
        var filter = args.Str("table_contains");
        var rows = snap.Privileges.Select(p => (Parsed: RoleDiff.ParsePrivilege(p.PrivilegeName), p.DepthMask))
            .GroupBy(x => x.Parsed.Table)
            .Where(g => filter is null || g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(g =>
            {
                var row = new Dictionary<string, object?> { ["table"] = g.Key };
                foreach (var x in g) row[x.Parsed.Access == "Misc" ? "misc" : x.Parsed.Access] = RoleDiff.DepthName(x.DepthMask);
                return row;
            }).OrderBy(r => (string)r["table"]!).ToList();
        return Results.Table($"Role '{snap.Roles[0].Name}' in {env.Name} has {snap.Privileges.Count} privileges across {rows.Count} tables/items.",
            rows, Results.Src(env, "role, roleprivileges, privilege"), topN: 60, uiTitle: $"Privileges: {snap.Roles[0].Name}");
    }
}
