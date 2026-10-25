namespace DvQna.Analysis;

/// <summary>A security role as read from one environment (root business unit copy only).</summary>
public sealed record RoleInfo(Guid RoleId, string Name, Guid? TemplateId, int? IsInherited, bool IsManaged);

/// <summary>One privilege granted to a role, identified by privilege name (never object type code).</summary>
public sealed record RolePrivilege(Guid RoleId, string PrivilegeName, int DepthMask);

/// <summary>Roles and their privileges in one environment. <c>DefinedPrivileges</c> lists every privilege that exists there (null = unknown).</summary>
public sealed record RoleSnapshot(string EnvName, IReadOnlyList<RoleInfo> Roles, IReadOnlyList<RolePrivilege> Privileges, IReadOnlySet<string>? DefinedPrivileges = null);

public sealed record RoleMatch(RoleInfo? A, RoleInfo? B, string MatchedBy);

public sealed record PrivilegeDiff(
    string Role, string Privilege, string Table, string Access,
    string? DepthA, string? DepthB, string Change, int Risk);

public sealed record RoleSettingDiff(string Role, string Setting, string? A, string? B);

public sealed record RoleDiffResult(
    IReadOnlyList<RoleMatch> Matches,
    IReadOnlyList<PrivilegeDiff> Privileges,
    IReadOnlyList<RoleSettingDiff> Settings);

/// <summary>Deterministic cross-environment role comparison (design section 9.5).</summary>
public static class RoleDiff
{
    private static readonly string[] AccessVerbs = ["AppendTo", "Append", "Assign", "Create", "Delete", "Read", "Share", "Write"];
    private static readonly HashSet<string> CoreTables = new(StringComparer.OrdinalIgnoreCase)
        { "Account", "Contact", "Lead", "Opportunity", "Incident", "SystemUser", "Role", "BusinessUnit", "Team" };

    /// <summary>privilegedepthmask: 1 = User, 2 = Business Unit, 4 = Parent:Child BU, 8 = Organization.</summary>
    public static string DepthName(int mask) => mask switch
    {
        >= 8 => "Organization",
        >= 4 => "Parent:Child BU",
        >= 2 => "Business Unit",
        >= 1 => "User",
        _ => "None",
    };

    private static int DepthRank(int mask) => mask >= 8 ? 4 : mask >= 4 ? 3 : mask >= 2 ? 2 : mask >= 1 ? 1 : 0;

    /// <summary>Splits "prvWritenew_project" into ("Write", "new_project"). Misc privileges return ("Misc", name).</summary>
    public static (string Access, string Table) ParsePrivilege(string name)
    {
        if (name.StartsWith("prv", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name[3..];
            foreach (var verb in AccessVerbs)
                if (rest.StartsWith(verb, StringComparison.Ordinal) && rest.Length > verb.Length)
                    return (verb, rest[verb.Length..]);
        }
        return ("Misc", name);
    }

    /// <summary>Match by roleid, then roletemplateid, then case-insensitive name.</summary>
    public static List<RoleMatch> Match(IReadOnlyList<RoleInfo> a, IReadOnlyList<RoleInfo> b)
    {
        var result = new List<RoleMatch>();
        var unmatchedB = b.ToList();
        foreach (var ra in a)
        {
            var hit = unmatchedB.FirstOrDefault(rb => rb.RoleId == ra.RoleId);
            var by = "roleid";
            if (hit is null && ra.TemplateId is { } t && t != Guid.Empty)
            {
                hit = unmatchedB.FirstOrDefault(rb => rb.TemplateId == t);
                by = "roletemplateid";
            }
            if (hit is null)
            {
                hit = unmatchedB.FirstOrDefault(rb => string.Equals(rb.Name.Trim(), ra.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                by = "name";
            }
            if (hit is not null) unmatchedB.Remove(hit);
            result.Add(new RoleMatch(ra, hit, hit is null ? "only in A" : by));
        }
        result.AddRange(unmatchedB.Select(rb => new RoleMatch(null, rb, "only in B")));
        return result;
    }

    public static RoleDiffResult Compare(RoleSnapshot a, RoleSnapshot b)
    {
        var matches = Match(a.Roles, b.Roles);
        var privA = a.Privileges.GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => ToMap(g));
        var privB = b.Privileges.GroupBy(p => p.RoleId).ToDictionary(g => g.Key, g => ToMap(g));
        var diffs = new List<PrivilegeDiff>();
        var settings = new List<RoleSettingDiff>();

        foreach (var m in matches.Where(m => m.A is not null && m.B is not null))
        {
            var roleName = m.A!.Name == m.B!.Name ? m.A.Name : $"{m.A.Name} / {m.B.Name}";
            if (m.A.Name != m.B.Name) settings.Add(new(roleName, "name (possible rename)", m.A.Name, m.B.Name));
            if (m.A.IsInherited != m.B.IsInherited)
                settings.Add(new(roleName, "member's privilege inheritance", Inherit(m.A.IsInherited), Inherit(m.B.IsInherited)));
            if (m.A.IsManaged != m.B.IsManaged)
                settings.Add(new(roleName, "managed", m.A.IsManaged.ToString(), m.B.IsManaged.ToString()));

            var pa = privA.GetValueOrDefault(m.A.RoleId) ?? new();
            var pb = privB.GetValueOrDefault(m.B.RoleId) ?? new();
            foreach (var name in pa.Keys.Union(pb.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var hasA = pa.TryGetValue(name, out var da);
                var hasB = pb.TryGetValue(name, out var db);
                if (hasA && hasB && DepthRank(da) == DepthRank(db)) continue;
                var (access, table) = ParsePrivilege(name);
                // A privilege that does not exist at all on one side means the table (solution) is missing there,
                // which is a component difference, not a permission difference.
                var missingTable = (!hasA && a.DefinedPrivileges is { } defA && !defA.Contains(name))
                                   || (!hasB && b.DefinedPrivileges is { } defB && !defB.Contains(name));
                var change = missingTable ? (!hasA ? "table not in A" : "table not in B")
                    : !hasA ? "only in B" : !hasB ? "only in A"
                    : DepthRank(db) > DepthRank(da) ? "higher in B" : "lower in B";
                diffs.Add(new PrivilegeDiff(roleName, name, table, access,
                    hasA ? DepthName(da) : null, hasB ? DepthName(db) : null, change,
                    missingTable ? 0 : Risk(access, table, hasA ? da : 0, hasB ? db : 0)));
            }
        }
        return new RoleDiffResult(matches, diffs.OrderByDescending(d => d.Risk).ThenBy(d => d.Role).ThenBy(d => d.Privilege).ToList(), settings);
    }

    private static Dictionary<string, int> ToMap(IEnumerable<RolePrivilege> ps)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ps) map[p.PrivilegeName] = Math.Max(map.GetValueOrDefault(p.PrivilegeName), p.DepthMask);
        return map;
    }

    private static string? Inherit(int? v) => v switch { 0 => "Team privileges only", 1 => "Direct user access and team privileges", _ => null };

    /// <summary>Risk score for ranking: how much more access the second environment grants, weighted by privilege type.</summary>
    internal static int Risk(string access, string table, int depthA, int depthB)
    {
        var delta = Math.Abs(DepthRank(depthB) - DepthRank(depthA));
        var weight = access switch
        {
            "Delete" or "Assign" or "Share" => 3,
            "Write" or "Create" => 2,
            "Misc" => table.Contains("Customization", StringComparison.OrdinalIgnoreCase) || table.Contains("BulkDelete", StringComparison.OrdinalIgnoreCase) || table.Contains("ExportToExcel", StringComparison.OrdinalIgnoreCase) ? 3 : 1,
            _ => 1,
        };
        if (CoreTables.Contains(table)) weight++;
        return delta * weight;
    }
}
