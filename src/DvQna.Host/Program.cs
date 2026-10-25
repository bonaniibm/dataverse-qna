using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DvQna.Agent;
using DvQna.Auth;
using DvQna.Connectors;
using DvQna.Core;
using DvQna.Host;
using DvQna.Storage;
using DvQna.Tools;
using HttpResults = Microsoft.AspNetCore.Http.Results;

var verb = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "serve";
var rest = args.SkipWhile(a => a.StartsWith('-')).Skip(1).ToArray();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args.Where(a => a.StartsWith("--")).ToArray(), ContentRootPath = AppContext.BaseDirectory });
var settings = new AppSettings();
builder.Configuration.Bind(settings);
var userConfig = Path.Combine(settings.DataDirectory, "config.json");
if (File.Exists(userConfig))
{
    builder.Configuration.AddJsonFile(userConfig, optional: true);
    builder.Configuration.Bind(settings);
}
builder.Configuration.AddEnvironmentVariables("DVQNA_");
builder.Configuration.Bind(settings);
Directory.CreateDirectory(settings.DataDirectory);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ITokenProvider>(sp => settings.Auth.Mode.Equals("azcli", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(settings.Auth.ClientId)
    ? new AzureCliTokenProvider()
    : new MsalTokenProvider(settings.Auth.ClientId!, settings.DataDirectory, sp.GetRequiredService<ILogger<MsalTokenProvider>>()) { UseBroker = settings.Auth.UseBroker });
builder.Services.AddSingleton<IDataverseReader, DataverseReader>();
builder.Services.AddSingleton<AppInsightsReader>();
builder.Services.AddSingleton<GlobalDiscovery>();
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<AppStore>();
builder.Services.AddSingleton<SecretStore>();
builder.Services.AddSingleton<EnvironmentProfileCache>();
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ToolContext>();
builder.Services.AddSingleton(sp =>
{
    var client = LlmClientFactory.Create(settings, sp.GetRequiredService<SecretStore>(), out var status);
    return new LlmState(client is null ? null : new Orchestrator(client, sp.GetRequiredService<ToolRegistry>(), sp.GetRequiredService<ToolContext>(),
        sp.GetRequiredService<AppStore>(), settings, sp.GetRequiredService<ILogger<Orchestrator>>()), status);
});
builder.Logging.SetMinimumLevel(LogLevel.Warning).AddFilter("DvQna", LogLevel.Information);

if (verb != "serve") builder.Logging.ClearProviders();
var port = builder.Configuration.GetValue("Port", 5178);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();

switch (verb)
{
    case "serve": break;
    case "tool": return await Cli.RunToolAsync(app.Services, rest);
    case "env": return await Cli.EnvAsync(app.Services, rest);
    case "discover": return await Cli.DiscoverAsync(app.Services, rest);
    case "set-foundry-key": return Cli.SetSecret(app.Services, "foundry-key");
    case "status":
        Console.WriteLine(app.Services.GetRequiredService<LlmState>().Status);
        return 0;
    default:
        Console.WriteLine("Usage: dvqna [serve|tool <name> [json]|env list|env add <name> <url> [tags]|env remove <name>|discover <tenantId>|set-foundry-key|status]");
        return 1;
}

// ---- Web host ----
var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
File.WriteAllText(Path.Combine(settings.DataDirectory, "session.json"), JsonSerializer.Serialize(new { port, token = sessionToken }));

app.Use(async (ctx, next) =>
{
    // Loopback only, and only callers that know the per-launch token (blocks other sites and DNS rebinding).
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 403; return; }
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        var supplied = ctx.Request.Headers["X-DvQna-Token"].FirstOrDefault() ?? ctx.Request.Query["t"].FirstOrDefault();
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(supplied ?? ""), System.Text.Encoding.UTF8.GetBytes(sessionToken)))
        { ctx.Response.StatusCode = 401; return; }
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
var jsonOut = Json.Options;

api.MapGet("/status", (LlmState llm, ProfileStore profiles, ToolRegistry reg) => HttpResults.Json(new
{
    llmReady = llm.Orchestrator is not null, llmStatus = llm.Status, authMode = string.IsNullOrEmpty(settings.Auth.ClientId) ? "azcli (no ClientId configured)" : settings.Auth.Mode,
    environments = profiles.All().Count, tools = reg.Tools.Select(t => new { t.Name, t.Description }),
}, jsonOut));

api.MapGet("/environments", (ProfileStore p) => HttpResults.Json(p.All(), jsonOut));
api.MapPost("/environments", async (EnvInput input, ProfileStore p, CancellationToken ct) =>
{
    var url = input.Url.Trim().TrimEnd('/');
    var tenant = input.TenantId ?? await GlobalDiscovery.ResolveTenantAsync(url, ct);
    if (tenant is null) return HttpResults.BadRequest(new { error = "Could not determine the tenant for this URL." });
    var profile = new EnvironmentProfile
    {
        Name = input.Name.Trim(), Url = url, TenantId = tenant, Tags = input.Tags ?? [],
        AppInsightsResourceId = input.AppInsightsResourceId, AppInsightsTenantId = input.AppInsightsTenantId,
        PrivacyMode = input.PrivacyMode ?? PrivacyMode.Operational,
    };
    p.Upsert(profile);
    return HttpResults.Json(profile, jsonOut);
});
api.MapDelete("/environments/{name}", (string name, ProfileStore p) => p.Remove(name) ? HttpResults.NoContent() : HttpResults.NotFound());
api.MapGet("/discover", async (string tenant, GlobalDiscovery d, CancellationToken ct) => HttpResults.Json(await d.ListAsync(tenant, ct), jsonOut));

api.MapPost("/tools/{name}", async (string name, JsonElement input, ToolRegistry reg, ToolContext ctx, AppStore store, CancellationToken ct) =>
{
    var tool = reg.Find(name);
    if (tool is null) return HttpResults.NotFound();
    try
    {
        var sw = Stopwatch.StartNew();
        var result = await tool.RunAsync(new ToolArgs(input), ctx, ct);
        if (result.FullData is { Count: > 0 } full) result.DatasetId = store.SaveDataset("direct", name, result.Sources.FirstOrDefault()?.Env ?? "", full, result.Sources);
        store.AuditToolCall("direct", name, input.GetRawText(), result, sw.ElapsedMilliseconds, null);
        return HttpResults.Json(new { result.Summary, result.RowCount, result.DatasetId, result.Warnings, result.Sources, result.Ui, rows = result.FullData ?? result.Data }, jsonOut);
    }
    catch (Exception ex) when (ex is ToolInputException or DataverseException or InvalidOperationException)
    {
        return HttpResults.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/chat", async (HttpContext http, ChatRequest req, LlmState llm, CancellationToken ct) =>
{
    if (llm.Orchestrator is null)
    {
        http.Response.StatusCode = 503;
        await http.Response.WriteAsJsonAsync(new { error = "LLM not configured: " + llm.Status }, ct);
        return;
    }
    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    var reader = llm.Orchestrator.Run(req, ct);
    await foreach (var evt in reader.ReadAllAsync(ct))
    {
        await http.Response.WriteAsync($"event: {evt.Type}\ndata: {JsonSerializer.Serialize(evt.Data, jsonOut)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
});

api.MapGet("/conversations", (AppStore s) => HttpResults.Json(s.Conversations(), jsonOut));
api.MapGet("/conversations/{id}", (string id, AppStore s) => HttpResults.Json(s.Messages(id), jsonOut));
api.MapGet("/datasets/{id}", (string id, AppStore s) => s.GetDataset(id) is { } d
    ? HttpResults.Json(new { d.Info, rows = d.Rows, sources = d.Sources }, jsonOut) : HttpResults.NotFound());

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = $"http://127.0.0.1:{port}/?t={sessionToken}";
    Console.WriteLine($"Dataverse QnA is running at {url}");
    Console.WriteLine($"LLM: {app.Services.GetRequiredService<LlmState>().Status}");
    if (!args.Contains("--no-browser"))
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
});
await app.RunAsync();
return 0;

public sealed record LlmState(Orchestrator? Orchestrator, string Status);

public sealed record EnvInput(string Name, string Url, string? TenantId, List<string>? Tags, string? AppInsightsResourceId, string? AppInsightsTenantId, PrivacyMode? PrivacyMode);
