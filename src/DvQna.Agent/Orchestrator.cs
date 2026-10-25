using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DvQna.Core;
using DvQna.Storage;
using DvQna.Tools;
using Microsoft.Extensions.Logging;

namespace DvQna.Agent;

public sealed class ChatRequest
{
    public string? ConversationId { get; set; }
    public required string Message { get; set; }
    public string? Env { get; set; }
    public string? CompareEnv { get; set; }
    public bool Deep { get; set; }
}

/// <summary>Event streamed to the UI over SSE.</summary>
public sealed record AgentEvent(string Type, object? Data);

/// <summary>
/// The agent loop (design section 8.2): the model picks tools, tools run read-only against Dataverse,
/// results are shaped for the model (redaction, top-N), full data goes to the dataset store and the UI.
/// Provider-neutral: Claude and OpenAI-compatible Foundry models plug in through <see cref="IModelProvider"/>.
/// </summary>
public sealed class Orchestrator
{
    private readonly IModelProvider _provider;
    private readonly ToolRegistry _registry;
    private readonly ToolContext _ctx;
    private readonly AppStore _store;
    private readonly LlmSettings _settings;
    private readonly ILogger<Orchestrator> _log;
    private readonly ConcurrentDictionary<string, IModelSession> _sessions = new();

    public Orchestrator(IModelProvider provider, ToolRegistry registry, ToolContext ctx, AppStore store, AppSettings settings, ILogger<Orchestrator> log)
    {
        _provider = provider;
        _registry = registry;
        _ctx = ctx;
        _store = store;
        _settings = settings.Llm;
        _log = log;
    }

    public ChannelReader<AgentEvent> Run(ChatRequest req, CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        _ = Task.Run(async () =>
        {
            try { await RunCoreAsync(req, channel.Writer, ct); }
            catch (OperationCanceledException) { channel.Writer.TryWrite(new("error", new { message = "Cancelled." })); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Agent turn failed");
                channel.Writer.TryWrite(new("error", new { message = ex.Message }));
            }
            finally { channel.Writer.TryComplete(); }
        }, ct);
        return channel.Reader;
    }

    private async Task RunCoreAsync(ChatRequest req, ChannelWriter<AgentEvent> output, CancellationToken ct)
    {
        var conversationId = req.ConversationId ?? Guid.NewGuid().ToString("N")[..12];
        var session = _sessions.GetOrAdd(conversationId, _ => _provider.NewSession(_registry.Tools));
        _store.TouchConversation(conversationId, req.Message.Length > 80 ? req.Message[..80] + "…" : req.Message);
        _store.AddMessage(conversationId, "user", req.Message);
        await output.WriteAsync(new("conversation", new { id = conversationId }), ct);

        session.AddUser(await BuildEnvironmentContextAsync(req, ct), req.Message);

        var toolCalls = 0;
        long input = 0, outputTokens = 0, cached = 0;
        var finalText = new List<string>();
        var privacy = (req.Env is not null ? _ctx.Profiles.Find(req.Env)?.PrivacyMode : null) ?? PrivacyMode.Operational;

        for (var turn = 0; turn < 40; turn++)
        {
            var step = await session.NextAsync(req.Deep, ct);
            input += step.InputTokens;
            outputTokens += step.OutputTokens;
            cached += step.CachedTokens;
            foreach (var t in step.Texts)
            {
                finalText.Add(t);
                await output.WriteAsync(new("text", new { text = t }), ct);
            }
            if (step.Refused)
            {
                await output.WriteAsync(new("text", new { text = "\n\n_The model declined to answer this request._" }), ct);
                break;
            }
            if (step.ToolCalls.Count == 0) break;

            toolCalls += step.ToolCalls.Count;
            if (toolCalls > _settings.MaxToolCallsPerTurn)
            {
                // Give the model a chance to summarise with what it has.
                session.AddToolResults(step.ToolCalls.Select(c => new ToolCallResult(c.Id,
                    "Tool budget for this question exhausted. Answer with the evidence gathered so far and say what was not checked.", true)).ToList());
                continue;
            }
            var results = await Task.WhenAll(step.ToolCalls.Select(c => ExecuteToolAsync(conversationId, c, privacy, output, ct)));
            session.AddToolResults(results);
        }

        _store.AddMessage(conversationId, "assistant", string.Join("\n\n", finalText));
        await output.WriteAsync(new("done", new
        {
            conversation_id = conversationId,
            tool_calls = toolCalls,
            model = _provider.Description,
            usage = new { input = input - cached, output = outputTokens, cache_read = cached, cache_write = 0 },
        }), ct);
    }

    private async Task<ToolCallResult> ExecuteToolAsync(string conversationId, ToolCallRequest call, PrivacyMode privacy, ChannelWriter<AgentEvent> output, CancellationToken ct)
    {
        await output.WriteAsync(new("tool_start", new { id = call.Id, name = call.Name, input = call.Input }), ct);
        var sw = Stopwatch.StartNew();
        var tool = _registry.Find(call.Name);
        try
        {
            if (tool is null) throw new ToolInputException($"Unknown tool '{call.Name}'.");
            var result = await tool.RunAsync(new ToolArgs(call.Input), _ctx, ct);
            var env = result.Sources.FirstOrDefault()?.Env ?? "";
            if (result.FullData is { Count: > 0 } full)
                result.DatasetId = _store.SaveDataset(conversationId, call.Name, env, full, result.Sources);
            _store.AuditToolCall(conversationId, call.Name, call.Input.GetRawText(), result, sw.ElapsedMilliseconds, null);
            await output.WriteAsync(new("tool_end", new
            {
                id = call.Id, name = call.Name, summary = result.Summary, row_count = result.RowCount, dataset_id = result.DatasetId,
                warnings = result.Warnings, sources = result.Sources, ui = result.Ui, ms = sw.ElapsedMilliseconds,
            }), ct);
            return new ToolCallResult(call.Id, Redactor.ForLlm(result, privacy), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.AuditToolCall(conversationId, call.Name, call.Input.GetRawText(), null, sw.ElapsedMilliseconds, ex.Message);
            await output.WriteAsync(new("tool_end", new { id = call.Id, name = call.Name, error = ex.Message, ms = sw.ElapsedMilliseconds }), ct);
            return new ToolCallResult(call.Id, "Tool failed: " + ex.Message, true);
        }
    }

    private async Task<string> BuildEnvironmentContextAsync(ChatRequest req, CancellationToken ct)
    {
        var lines = new List<string> { "<environment_context>", $"Current UTC time: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z" };
        var all = _ctx.Profiles.All();
        lines.Add("Configured environments: " + (all.Count == 0 ? "(none)" : string.Join(", ", all.Select(p => $"{p.Name} [{string.Join(",", p.Tags)}]"))));
        foreach (var (label, name) in new[] { ("Active environment", req.Env), ("Comparison environment", req.CompareEnv) })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var env = _ctx.Profiles.Find(name);
            if (env is null) { lines.Add($"{label}: {name} (not configured)"); continue; }
            lines.Add($"{label}: {env.Name} ({env.Url})");
            try
            {
                var p = await _ctx.ProfileCache.GetAsync(env, false, ct);
                lines.Add($"  version {p.Version}; caller {p.CallerName}{(p.CallerIsSystemAdmin ? " (System Administrator)" : "")}; trace log setting {p.PluginTraceLogSetting}; App Insights {(p.AppInsightsLinked ? "linked" : "not linked")}");
                foreach (var n in p.Notes) lines.Add("  note: " + n);
            }
            catch (Exception ex) { lines.Add($"  capability profile unavailable: {ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(req.Env)) lines.Add("No active environment selected in the UI.");
        lines.Add("</environment_context>");
        return string.Join("\n", lines);
    }
}
