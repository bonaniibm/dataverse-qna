using System.Text.Json;
using DvQna.Connectors;
using DvQna.Core;
using DvQna.Storage;
using DvQna.Tools;

namespace DvQna.Host;

/// <summary>Command-line verbs: handy for scripting and for testing tools without an LLM.</summary>
internal static class Cli
{
    public static async Task<int> RunToolAsync(IServiceProvider sp, string[] args)
    {
        var reg = sp.GetRequiredService<ToolRegistry>();
        if (args.Length == 0)
        {
            foreach (var t in reg.Tools) Console.WriteLine($"{t.Name,-26} {t.Description}");
            return 0;
        }
        var tool = reg.Find(args[0]);
        if (tool is null) { Console.Error.WriteLine($"Unknown tool {args[0]}"); return 1; }
        var json = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "{}";
        using var doc = JsonDocument.Parse(json);
        try
        {
            var result = await tool.RunAsync(new ToolArgs(doc.RootElement), sp.GetRequiredService<ToolContext>(), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new { result.Summary, result.RowCount, result.Truncated, result.Warnings, result.Sources, rows = result.Data }, Json.Pretty));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    public static async Task<int> EnvAsync(IServiceProvider sp, string[] args)
    {
        var store = sp.GetRequiredService<ProfileStore>();
        switch (args.FirstOrDefault())
        {
            case "add" when args.Length >= 3:
                var url = args[2].TrimEnd('/');
                var tenant = await GlobalDiscovery.ResolveTenantAsync(url, CancellationToken.None)
                    ?? throw new InvalidOperationException("Could not resolve tenant for " + url);
                store.Upsert(new EnvironmentProfile { Name = args[1], Url = url, TenantId = tenant, Tags = args.Skip(3).ToList() });
                Console.WriteLine($"Added {args[1]} ({url}, tenant {tenant})");
                return 0;
            case "remove" when args.Length >= 2:
                Console.WriteLine(store.Remove(args[1]) ? "Removed." : "Not found.");
                return 0;
            default:
                foreach (var p in store.All()) Console.WriteLine($"{p.Name,-16} {p.Url,-45} {p.TenantId} [{string.Join(",", p.Tags)}]");
                return 0;
        }
    }

    public static async Task<int> DiscoverAsync(IServiceProvider sp, string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: dvqna discover <tenantId>"); return 1; }
        foreach (var e in await sp.GetRequiredService<GlobalDiscovery>().ListAsync(args[0], CancellationToken.None))
            Console.WriteLine($"{e.FriendlyName,-30} {e.Url,-45} {e.Region} {e.Version} {e.State}");
        return 0;
    }

    /// <summary>dvqna ask &lt;env&gt; "question" — runs one full agent turn and prints tool activity and the answer.</summary>
    public static async Task<int> AskAsync(IServiceProvider sp, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: dvqna ask <env> \"question\""); return 1; }
        var llm = sp.GetRequiredService<LlmState>();
        if (llm.Orchestrator is null) { Console.Error.WriteLine("LLM not configured: " + llm.Status); return 1; }
        Console.WriteLine($"[model] {llm.Status}");
        var reader = llm.Orchestrator.Run(new DvQna.Agent.ChatRequest { Env = args[0], Message = string.Join(' ', args.Skip(1)) }, CancellationToken.None);
        await foreach (var e in reader.ReadAllAsync())
        {
            var json = JsonSerializer.SerializeToElement(e.Data, Json.Options);
            string P(string n) => json.TryGetProperty(n, out var v) ? v.ToString() : "";
            switch (e.Type)
            {
                case "tool_start": Console.WriteLine($"[tool] {P("name")} {P("input")}"); break;
                case "tool_end": Console.WriteLine($"   -> {(P("error") is { Length: > 0 } err ? "ERROR " + err : P("summary"))} ({P("ms")} ms)"); break;
                case "text": Console.WriteLine("\n" + P("text")); break;
                case "error": Console.WriteLine("[error] " + P("message")); break;
                case "done": Console.WriteLine($"\n[done] {P("tool_calls")} tool call(s), usage {P("usage")}"); break;
            }
        }
        return 0;
    }

    public static int SetSecret(IServiceProvider sp, string name)
    {
        Console.Write($"Paste the value for '{name}' and press Enter: ");
        var value = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(value)) return 1;
        sp.GetRequiredService<SecretStore>().Set(name, value.Trim());
        Console.WriteLine("Saved (encrypted for the current Windows user).");
        return 0;
    }
}
