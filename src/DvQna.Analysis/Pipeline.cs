namespace DvQna.Analysis;

/// <summary>One thing that runs for a table + message: a plug-in step, classic workflow, business rule or flow trigger.</summary>
public sealed record PipelineItem
{
    public required string Kind { get; init; }            // PluginStep | Workflow | BusinessRule | FlowTrigger
    public required string Name { get; init; }
    public string? Handler { get; init; }
    public int Stage { get; init; }                        // 10 PreValidation, 20 PreOperation, 40 PostOperation
    public string Mode { get; init; } = "Sync";            // Sync | Async
    public int Rank { get; init; }
    public string? FilteringAttributes { get; init; }
    public bool Enabled { get; init; } = true;
    public bool? AsyncAutoDelete { get; init; }
    public string? Images { get; init; }
    public string? Solution { get; init; }
    public bool AnyEntity { get; init; }
    public List<string> Flags { get; init; } = [];
}

public static class PipelineBuilder
{
    public static string StageName(int stage) => stage switch
    {
        10 => "PreValidation",
        20 => "PreOperation",
        30 => "MainOperation",
        40 => "PostOperation",
        _ => stage.ToString(),
    };

    /// <summary>Orders items the way the platform runs them and attaches anti-pattern flags.</summary>
    public static List<PipelineItem> Build(IEnumerable<PipelineItem> items, string message)
    {
        var list = items.ToList();
        var result = new List<PipelineItem>();
        foreach (var i in list)
        {
            var flags = new List<string>(i.Flags);
            var isUpdate = message.Equals("Update", StringComparison.OrdinalIgnoreCase);
            var isRetrieve = message.StartsWith("Retrieve", StringComparison.OrdinalIgnoreCase);
            if (i.Enabled && i.Kind is "PluginStep" or "FlowTrigger" && isUpdate && string.IsNullOrWhiteSpace(i.FilteringAttributes))
                flags.Add(i.Mode == "Sync" ? "PLG-001 sync Update step without filtering attributes" : "No filtering attributes: runs on every update");
            if (i.Enabled && i.Kind == "PluginStep" && i.Mode == "Sync" && isRetrieve)
                flags.Add("PLG-002 sync step on " + message);
            if (i.Enabled && i.Kind == "PluginStep" && i.Mode == "Async" && i.AsyncAutoDelete == false)
                flags.Add("PLG-003 async step without auto-delete");
            if (i.Kind == "Workflow" && i.Mode == "Sync" && i.Enabled)
                flags.Add("WF-001 real-time classic workflow");
            if (!i.Enabled) flags.Add("disabled");
            result.Add(i with { Flags = flags });
        }

        // PLG-004: many sync steps in the same stage; PLG-007: duplicate registrations.
        foreach (var g in result.Where(r => r.Enabled && r.Kind == "PluginStep" && r.Mode == "Sync").GroupBy(r => r.Stage).Where(g => g.Count() > 5))
            foreach (var r in g) r.Flags.Add($"PLG-004 {g.Count()} sync steps in {StageName(g.Key)}");
        foreach (var g in result.Where(r => r.Kind == "PluginStep" && r.Handler is not null).GroupBy(r => (r.Handler, r.Stage, r.Mode)).Where(g => g.Count() > 1))
            foreach (var r in g) r.Flags.Add("PLG-007 duplicate registration");

        return result
            .OrderBy(r => r.Kind == "FlowTrigger" ? 1 : 0)
            .ThenBy(r => r.Stage)
            .ThenBy(r => r.Mode == "Sync" ? 0 : 1)
            .ThenBy(r => r.Rank)
            .ThenBy(r => r.Name)
            .ToList();
    }
}
