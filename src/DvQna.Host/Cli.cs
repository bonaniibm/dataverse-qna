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
