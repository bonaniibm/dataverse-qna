using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Anthropic;
using Anthropic.Models.Messages;
using DvQna.Core;
using DvQna.Storage;
using DvQna.Tools;
using Microsoft.Extensions.Logging;
using AnthropicTool = Anthropic.Models.Messages.Tool;

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
/// The agent loop (design section 8.2): Claude picks tools, tools run read-only against Dataverse,
/// results are shaped for the model (redaction, top-N), full data goes to the dataset store and the UI.
/// </summary>
public sealed class Orchestrator
{
    private readonly IAnthropicClient _client;
    private readonly ToolRegistry _registry;
    private readonly ToolContext _ctx;
    private readonly AppStore _store;
    private readonly LlmSettings _settings;
    private readonly ILogger<Orchestrator> _log;
    private readonly ConcurrentDictionary<string, List<MessageParam>> _history = new();
    private readonly List<ToolUnion> _tools;

    public Orchestrator(IAnthropicClient client, ToolRegistry registry, ToolContext ctx, AppStore store, AppSettings settings, ILogger<Orchestrator> log)
    {
        _client = client;
        _registry = registry;
        _ctx = ctx;
        _store = store;
        _settings = settings.Llm;
        _log = log;
        _tools = registry.Tools.Select(ToApiTool).ToList();
        // Cache breakpoint on the last tool: tools + system prompt form the stable prefix.
        if (_tools.Count > 0 && _tools[^1].TryPickTool(out var last))
            _tools[^1] = last with { CacheControl = new CacheControlEphemeral() };
    }

    private static ToolUnion ToApiTool(DvQna.Tools.Tool t)
    {
        var props = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in (JsonObject)t.Schema["properties"]!)
            props[k] = JsonSerializer.SerializeToElement(v);
        var required = ((JsonArray)t.Schema["required"]!).Select(n => n!.GetValue<string>()).ToList();
        return new AnthropicTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
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
        var history = _history.GetOrAdd(conversationId, _ => []);
        _store.TouchConversation(conversationId, req.Message.Length > 80 ? req.Message[..80] + "…" : req.Message);
        _store.AddMessage(conversationId, "user", req.Message);
        await output.WriteAsync(new("conversation", new { id = conversationId }), ct);

        var envContext = await BuildEnvironmentContextAsync(req, ct);
        history.Add(new UserMessageParam
        {
            Content = new List<ContentBlockParam> { new TextBlockParam { Text = envContext }, new TextBlockParam { Text = req.Message } },
        });

        var toolCalls = 0;
        var usage = new UsageTotals();
        var finalText = new List<string>();
        var privacy = (req.Env is not null ? _ctx.Profiles.Find(req.Env)?.PrivacyMode : null) ?? PrivacyMode.Operational;

        for (var turn = 0; turn < 40; turn++)
        {
            var parameters = new MessageCreateParams
            {
                Model = _settings.Deployment,
                MaxTokens = 16000,
                System = new List<TextBlockParam> { new() { Text = SystemPrompt.Text, CacheControl = new CacheControlEphemeral() } },
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = req.Deep ? Effort.High : ParseEffort(_settings.Effort) },
                Tools = _tools,
                Messages = history,
            };
            var response = await _client.Messages.Create(parameters, ct);
            usage.Add(response.Usage);

            var assistant = new List<ContentBlockParam>();
            var pending = new List<ToolUseBlock>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistant.Add(new TextBlockParam { Text = text.Text });
                    finalText.Add(text.Text);
                    await output.WriteAsync(new("text", new { text = text.Text }), ct);
                }
                else if (block.TryPickThinking(out var thinking))
                {
                    assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out var toolUse))
                {
                    assistant.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                    pending.Add(toolUse);
                }
            }
            history.Add(new AssistantMessageParam { Content = assistant });

            var stop = response.StopReason?.ToString() ?? "";
            if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteAsync(new("text", new { text = "\n\n_The model declined to answer this request._" }), ct);
                break;
            }
            if (pending.Count == 0) break;

            toolCalls += pending.Count;
            if (toolCalls > _settings.MaxToolCallsPerTurn)
            {
                // Give the model a chance to summarise with what it has.
                history.Add(new UserMessageParam
        {
            Content = pending.Select(p => (ContentBlockParam)new ToolResultBlockParam
                    {
                        ToolUseID = p.ID, Content = "Tool budget for this question exhausted. Answer with the evidence gathered so far and say what was not checked.", IsError = true,
                    }).ToList(),
                });
                continue;
            }

            var results = await Task.WhenAll(pending.Select(p => ExecuteToolAsync(conversationId, p, privacy, output, ct)));
            history.Add(new UserMessageParam
        {
            Content = results.Select(r => (ContentBlockParam)r).ToList() });
        }

        var answer = string.Join("\n\n", finalText);
        _store.AddMessage(conversationId, "assistant", answer);
        await output.WriteAsync(new("done", new
        {
            conversation_id = conversationId,
            tool_calls = toolCalls,
            usage = new { input = usage.Input, output = usage.Output, cache_read = usage.CacheRead, cache_write = usage.CacheWrite },
        }), ct);
    }

    private async Task<ToolResultBlockParam> ExecuteToolAsync(string conversationId, ToolUseBlock call, PrivacyMode privacy, ChannelWriter<AgentEvent> output, CancellationToken ct)
    {
        var input = JsonSerializer.SerializeToElement(call.Input);
        await output.WriteAsync(new("tool_start", new { id = call.ID, name = call.Name, input }), ct);
        var sw = Stopwatch.StartNew();
        var tool = _registry.Find(call.Name);
        try
        {
            if (tool is null) throw new ToolInputException($"Unknown tool '{call.Name}'.");
            var result = await tool.RunAsync(new ToolArgs(input), _ctx, ct);
            var env = result.Sources.FirstOrDefault()?.Env ?? "";
            if (result.FullData is { Count: > 0 } full)
                result.DatasetId = _store.SaveDataset(conversationId, call.Name, env, full, result.Sources);
            _store.AuditToolCall(conversationId, call.Name, input.GetRawText(), result, sw.ElapsedMilliseconds, null);
            await output.WriteAsync(new("tool_end", new
            {
                id = call.ID, name = call.Name, summary = result.Summary, row_count = result.RowCount, dataset_id = result.DatasetId,
                warnings = result.Warnings, sources = result.Sources, ui = result.Ui, ms = sw.ElapsedMilliseconds,
            }), ct);
            return new ToolResultBlockParam { ToolUseID = call.ID, Content = Redactor.ForLlm(result, privacy) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _store.AuditToolCall(conversationId, call.Name, input.GetRawText(), null, sw.ElapsedMilliseconds, ex.Message);
            await output.WriteAsync(new("tool_end", new { id = call.ID, name = call.Name, error = ex.Message, ms = sw.ElapsedMilliseconds }), ct);
            return new ToolResultBlockParam { ToolUseID = call.ID, Content = "Tool failed: " + ex.Message, IsError = true };
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

    private static Effort ParseEffort(string s) => s.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    private sealed class UsageTotals
    {
        public long Input, Output, CacheRead, CacheWrite;
        public void Add(Usage u)
        {
            Input += u.InputTokens;
            Output += u.OutputTokens;
            CacheRead += u.CacheReadInputTokens ?? 0;
            CacheWrite += u.CacheCreationInputTokens ?? 0;
        }
    }
}
