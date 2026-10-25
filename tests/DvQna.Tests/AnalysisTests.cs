using DvQna.Analysis;

namespace DvQna.Tests;

public class RoleDiffTests
{
    private static readonly Guid SalesId = Guid.NewGuid();

    [Theory]
    [InlineData(1, "User")]
    [InlineData(2, "Business Unit")]
    [InlineData(4, "Parent:Child BU")]
    [InlineData(8, "Organization")]
    public void Depth_masks_map_to_access_levels(int mask, string expected) => Assert.Equal(expected, RoleDiff.DepthName(mask));

    [Theory]
    [InlineData("prvReadAccount", "Read", "Account")]
    [InlineData("prvAppendToContact", "AppendTo", "Contact")]
    [InlineData("prvAppendContact", "Append", "Contact")]
    [InlineData("prvWritenew_project", "Write", "new_project")]
    [InlineData("prvExportToExcel", "Misc", "prvExportToExcel")]
    public void Privilege_names_are_split_into_access_and_table(string name, string access, string table)
    {
        var (a, t) = RoleDiff.ParsePrivilege(name);
        Assert.Equal(access, a);
        Assert.Equal(table, t);
    }

    [Fact]
    public void Roles_match_by_id_then_template_then_name()
    {
        var template = Guid.NewGuid();
        var a = new[]
        {
            new RoleInfo(SalesId, "Sales", null, 1, true),
            new RoleInfo(Guid.NewGuid(), "System Customizer", template, 1, true),
            new RoleInfo(Guid.NewGuid(), "Support Agent", null, 1, false),
            new RoleInfo(Guid.NewGuid(), "Only In A", null, 1, false),
        };
        var b = new[]
        {
            new RoleInfo(SalesId, "Sales Renamed", null, 1, true),
            new RoleInfo(Guid.NewGuid(), "Customizer", template, 1, true),
            new RoleInfo(Guid.NewGuid(), "support agent", null, 1, false),
            new RoleInfo(Guid.NewGuid(), "Only In B", null, 1, false),
        };
        var m = RoleDiff.Match(a, b);
        Assert.Equal("roleid", m.Single(x => x.A?.Name == "Sales").MatchedBy);
        Assert.Equal("roletemplateid", m.Single(x => x.A?.Name == "System Customizer").MatchedBy);
        Assert.Equal("name", m.Single(x => x.A?.Name == "Support Agent").MatchedBy);
        Assert.Equal("only in A", m.Single(x => x.A?.Name == "Only In A").MatchedBy);
        Assert.Equal("only in B", m.Single(x => x.B?.Name == "Only In B").MatchedBy);
    }

    [Fact]
    public void Diff_reports_added_removed_and_depth_changes_ranked_by_risk()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var a = new RoleSnapshot("UAT", [new RoleInfo(idA, "Sales", null, 1, true)],
        [
            new RolePrivilege(idA, "prvReadAccount", 2),
            new RolePrivilege(idA, "prvDeleteAccount", 2),
            new RolePrivilege(idA, "prvReadLead", 8),
            new RolePrivilege(idA, "prvWriteContact", 1),
        ]);
        var b = new RoleSnapshot("PROD", [new RoleInfo(idB, "sales", null, 0, true)],
        [
            new RolePrivilege(idB, "prvReadAccount", 2),
            new RolePrivilege(idB, "prvDeleteAccount", 8),
            new RolePrivilege(idB, "prvReadLead", 8),
            new RolePrivilege(idB, "prvShareAccount", 1),
        ]);

        var diff = RoleDiff.Compare(a, b);

        Assert.Equal(3, diff.Privileges.Count);
        var top = diff.Privileges[0];
        Assert.Equal("prvDeleteAccount", top.Privilege);
        Assert.Equal("higher in B", top.Change);
        Assert.Equal("Business Unit", top.DepthA);
        Assert.Equal("Organization", top.DepthB);
        Assert.Contains(diff.Privileges, p => p.Privilege == "prvWriteContact" && p.Change == "only in A");
        Assert.Contains(diff.Privileges, p => p.Privilege == "prvShareAccount" && p.Change == "only in B");
        Assert.Contains(diff.Settings, s => s.Setting == "member's privilege inheritance");
        Assert.Contains(diff.Settings, s => s.Setting.StartsWith("name"));
    }

    [Fact]
    public void Identical_roles_produce_no_differences()
    {
        var id = Guid.NewGuid();
        var s = new RoleSnapshot("X", [new RoleInfo(id, "R", null, 1, true)], [new RolePrivilege(id, "prvReadAccount", 8)]);
        var diff = RoleDiff.Compare(s, s with { EnvName = "Y" });
        Assert.Empty(diff.Privileges);
        Assert.Empty(diff.Settings);
    }
}

public class PipelineTests
{
    [Fact]
    public void Orders_by_stage_mode_rank_and_flags_antipatterns()
    {
        var items = new[]
        {
            new PipelineItem { Kind = "PluginStep", Name = "Post async", Handler = "A", Stage = 40, Mode = "Async", AsyncAutoDelete = false },
            new PipelineItem { Kind = "PluginStep", Name = "Pre sync no filter", Handler = "B", Stage = 20, Mode = "Sync", Rank = 2 },
            new PipelineItem { Kind = "PluginStep", Name = "Pre sync filtered", Handler = "C", Stage = 20, Mode = "Sync", Rank = 1, FilteringAttributes = "name" },
            new PipelineItem { Kind = "PluginStep", Name = "Validation", Handler = "D", Stage = 10, Mode = "Sync", FilteringAttributes = "x" },
            new PipelineItem { Kind = "Workflow", Name = "Realtime WF", Stage = 40, Mode = "Sync", FilteringAttributes = "x" },
        };
        var result = PipelineBuilder.Build(items, "Update");
        Assert.Equal(["Validation", "Pre sync filtered", "Pre sync no filter", "Realtime WF", "Post async"], result.Select(r => r.Name));
        Assert.Contains(result.Single(r => r.Name == "Pre sync no filter").Flags, f => f.StartsWith("PLG-001"));
        Assert.DoesNotContain(result.Single(r => r.Name == "Pre sync filtered").Flags, f => f.StartsWith("PLG-001"));
        Assert.Contains(result.Single(r => r.Name == "Post async").Flags, f => f.StartsWith("PLG-003"));
        Assert.Contains(result.Single(r => r.Name == "Realtime WF").Flags, f => f.StartsWith("WF-001"));
    }

    [Fact]
    public void Flags_sync_steps_on_retrieve_multiple()
    {
        var result = PipelineBuilder.Build([new PipelineItem { Kind = "PluginStep", Name = "Rm", Stage = 40, Mode = "Sync" }], "RetrieveMultiple");
        Assert.Contains(result[0].Flags, f => f.StartsWith("PLG-002"));
    }

    [Fact]
    public void Flags_duplicate_registrations()
    {
        var a = new PipelineItem { Kind = "PluginStep", Name = "One", Handler = "X.Plugin", Stage = 40, Mode = "Sync", FilteringAttributes = "a" };
        var result = PipelineBuilder.Build([a, a with { Name = "Two" }], "Update");
        Assert.All(result, r => Assert.Contains(r.Flags, f => f.StartsWith("PLG-007")));
    }
}

public class StatsTests
{
    [Fact]
    public void Percentiles_use_nearest_rank()
    {
        var s = Stats.Summarize(Enumerable.Range(1, 100).Select(i => (double)i));
        Assert.Equal(100, s.Count);
        Assert.Equal(50, s.P50);
        Assert.Equal(95, s.P95);
        Assert.Equal(100, s.Max);
    }

    [Fact]
    public void Empty_set_is_zero() => Assert.Equal(0, Stats.Summarize([]).Count);
}
