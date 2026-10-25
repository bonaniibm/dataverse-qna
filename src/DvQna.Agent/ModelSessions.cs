using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using DvQna.Core;
using OpenAI;
using OpenAI.Chat;
using AnthropicTool = Anthropic.Models.Messages.Tool;
using DvTool = DvQna.Tools.Tool;

namespace DvQna.Agent;

public sealed record ToolCallRequest(string Id, string Name, JsonElement Input);

public sealed record ModelTurn(IReadOnlyList<string> Texts, IReadOnlyList<ToolCallRequest> ToolCalls, bool Refused, long InputTokens, long OutputTokens, long CachedTokens);

public sealed record ToolCallResult(string Id, string Content, bool IsError);

/// <summary>
/// One conversation with one model. History is provider-specific (Claude needs its signed thinking blocks back,
/// OpenAI-style models need their tool-call messages), so each provider keeps its own.
/// </summary>
public interface IModelSession
{
    void AddUser(string environmentContext, string message);
    Task<ModelTurn> NextAsync(bool deep, CancellationToken ct);
    void AddToolResults(IReadOnlyList<ToolCallResult> results);
}

public interface IModelProvider
{
    string Description { get; }
    IModelSession NewSession(IReadOnlyList<DvTool> tools);
}

// ---------------- Claude (Anthropic SDK, Foundry or first-party) ----------------

public sealed class ClaudeProvider(IAnthropicClient client, LlmSettings settings, string description) : IModelProvider
{
    public string Description { get; } = description;
    public IModelSession NewSession(IReadOnlyList<DvTool> tools) => new ClaudeSession(client, settings, tools);
}

internal sealed class ClaudeSession : IModelSession
{
    private readonly IAnthropicClient _client;
    private readonly LlmSettings _settings;
    private readonly List<ToolUnion> _tools;
    private readonly List<MessageParam> _history = [];

    public ClaudeSession(IAnthropicClient client, LlmSettings settings, IReadOnlyList<DvTool> tools)
    {
        _client = client;
        _settings = settings;
        _tools = tools.Select(ToApiTool).ToList();
        // Cache breakpoint on the last tool: tools + system prompt form the stable prefix.
        if (_tools.Count > 0 && _tools[^1].TryPickTool(out var last))
            _tools[^1] = last with { CacheControl = new CacheControlEphemeral() };
    }

    private static ToolUnion ToApiTool(DvTool t)
    {
        var props = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in (JsonObject)t.Schema["properties"]!)
            props[k] = JsonSerializer.SerializeToElement(v);
        var required = ((JsonArray)t.Schema["required"]!).Select(n => n!.GetValue<string>()).ToList();
        return new AnthropicTool { Name = t.Name, Description = t.Description, InputSchema = new() { Properties = props, Required = required } };
    }

    public void AddUser(string environmentContext, string message) => _history.Add(new UserMessageParam
    {
        Content = new List<ContentBlockParam> { new TextBlockParam { Text = environmentContext }, new TextBlockParam { Text = message } },
    });

    public async Task<ModelTurn> NextAsync(bool deep, CancellationToken ct)
    {
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = _settings.Deployment,
            MaxTokens = 16000,
            System = new List<TextBlockParam> { new() { Text = SystemPrompt.Text, CacheControl = new CacheControlEphemeral() } },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = deep ? Effort.High : ParseEffort(_settings.Effort) },
            Tools = _tools,
            Messages = _history,
        }, ct);

        var assistant = new List<ContentBlockParam>();
        var texts = new List<string>();
        var calls = new List<ToolCallRequest>();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text)) { assistant.Add(new TextBlockParam { Text = text.Text }); texts.Add(text.Text); }
            else if (block.TryPickThinking(out var th)) assistant.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (block.TryPickRedactedThinking(out var rt)) assistant.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            else if (block.TryPickToolUse(out var tu))
            {
                assistant.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                calls.Add(new ToolCallRequest(tu.ID, tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
            }
        }
        _history.Add(new AssistantMessageParam { Content = assistant });
        var refused = (response.StopReason?.ToString() ?? "").Contains("refusal", StringComparison.OrdinalIgnoreCase);
        var u = response.Usage;
        return new ModelTurn(texts, calls, refused, u.InputTokens + (u.CacheReadInputTokens ?? 0), u.OutputTokens, u.CacheReadInputTokens ?? 0);
    }

    public void AddToolResults(IReadOnlyList<ToolCallResult> results) => _history.Add(new UserMessageParam
    {
        Content = results.Select(r => (ContentBlockParam)new ToolResultBlockParam { ToolUseID = r.Id, Content = r.Content, IsError = r.IsError }).ToList(),
    });

    private static Effort ParseEffort(string s) => s.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}

// ---------------- OpenAI-compatible models on Foundry (Grok, gpt-oss, DeepSeek, …) ----------------

public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly ChatClient _client;
    private readonly LlmSettings _settings;

    public OpenAiCompatibleProvider(ChatClient client, LlmSettings settings, string description)
    {
        _client = client;
        _settings = settings;
        Description = description;
    }

    public string Description { get; }

    public IModelSession NewSession(IReadOnlyList<DvTool> tools) => new OpenAiSession(_client, _settings, tools);

    /// <summary>Foundry's OpenAI-compatible v1 endpoint for a resource.</summary>
    public static Uri Endpoint(string resource) => new($"https://{resource}.openai.azure.com/openai/v1/");

    /// <summary>Adds a bearer token from an <see cref="ITokenProvider"/> to every request.</summary>
    public sealed class TokenPolicy(ITokenProvider tokens, string tenant, string scope) : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("Authorization", "Bearer " + tokens.GetTokenAsync(tenant, [scope], CancellationToken.None).GetAwaiter().GetResult());
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("Authorization", "Bearer " + await tokens.GetTokenAsync(tenant, [scope], CancellationToken.None));
            await ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}

internal sealed class OpenAiSession : IModelSession
{
    private readonly ChatClient _client;
    private readonly LlmSettings _settings;
    private readonly List<ChatTool> _tools;
    private readonly List<ChatMessage> _history = [new SystemChatMessage(SystemPrompt.Text)];

    public OpenAiSession(ChatClient client, LlmSettings settings, IReadOnlyList<DvTool> tools)
    {
        _client = client;
        _settings = settings;
        _tools = tools.Select(t => ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.Schema.ToJsonString()))).ToList();
    }

    public void AddUser(string environmentContext, string message) => _history.Add(new UserChatMessage(environmentContext + "\n\n" + message));

    public async Task<ModelTurn> NextAsync(bool deep, CancellationToken ct)
    {
        var options = new ChatCompletionOptions { MaxOutputTokenCount = 16000 };
        foreach (var t in _tools) options.Tools.Add(t);
        ChatCompletion completion = await _client.CompleteChatAsync(_history, options, ct);
        _history.Add(new AssistantChatMessage(completion));

        var texts = completion.Content.Where(p => p.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrWhiteSpace(p.Text)).Select(p => p.Text).ToList();
        var calls = completion.ToolCalls.Select(c => new ToolCallRequest(c.Id, c.FunctionName, ParseArgs(c.FunctionArguments))).ToList();
        var refused = !string.IsNullOrEmpty(completion.Refusal);
        if (refused) texts.Add(completion.Refusal);
        var u = completion.Usage;
        return new ModelTurn(texts, calls, refused, u?.InputTokenCount ?? 0, u?.OutputTokenCount ?? 0, u?.InputTokenDetails?.CachedTokenCount ?? 0);
    }

    private static JsonElement ParseArgs(BinaryData args)
    {
        try { return JsonDocument.Parse(args.ToString().Length == 0 ? "{}" : args.ToString()).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    public void AddToolResults(IReadOnlyList<ToolCallResult> results)
    {
        foreach (var r in results) _history.Add(new ToolChatMessage(r.Id, r.IsError ? "ERROR: " + r.Content : r.Content));
    }
}
