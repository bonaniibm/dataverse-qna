using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Anthropic.Foundry;
using Azure.Core;
using DvQna.Auth;
using DvQna.Core;
using DvQna.Agent;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace DvQna.Host;

/// <summary>Secrets (Foundry key) encrypted with Windows DPAPI for the current user.</summary>
public sealed class SecretStore(AppSettings settings)
{
    private string PathFor(string name) => Path.Combine(settings.DataDirectory, "secrets", name + ".bin");

    public void Set(string name, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathFor(name))!);
        var data = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Secret storage currently requires Windows DPAPI.");
        File.WriteAllBytes(PathFor(name), data);
    }

    public string? Get(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p) || !OperatingSystem.IsWindows()) return null;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser));
    }
}

public static class LlmClientFactory
{
    /// <summary>
    /// Builds the model provider from settings. Returns null when the LLM isn't configured yet; the UI then shows setup guidance.
    /// Providers: "foundry" (Claude on Foundry, the design's default), "foundry-openai" (OpenAI-compatible Foundry models
    /// such as Grok or gpt-oss), "anthropic" (developer fallback).
    /// </summary>
    public static IModelProvider? CreateProvider(AppSettings settings, SecretStore secrets, out string status)
    {
        var llm = settings.Llm;
        if (llm.Provider.Equals("foundry-openai", StringComparison.OrdinalIgnoreCase))
            return CreateOpenAiCompatible(llm, secrets, out status);
        var client = Create(settings, secrets, out status);
        return client is null ? null : new ClaudeProvider(client, llm, status);
    }

    private static IModelProvider? CreateOpenAiCompatible(LlmSettings llm, SecretStore secrets, out string status)
    {
        if (string.IsNullOrWhiteSpace(llm.FoundryResource))
        {
            status = "Foundry resource not configured (Llm:FoundryResource).";
            return null;
        }
        var options = new OpenAIClientOptions { Endpoint = OpenAiCompatibleProvider.Endpoint(llm.FoundryResource) };
        ChatClient chat;
        if (llm.FoundryAuth.Equals("apikey", StringComparison.OrdinalIgnoreCase))
        {
            var key = Environment.GetEnvironmentVariable("DVQNA_FOUNDRY_KEY") ?? secrets.Get("foundry-key");
            if (key is null) { status = "Foundry API key not set. Run: dvqna set-foundry-key"; return null; }
            chat = new ChatClient(llm.Deployment, new ApiKeyCredential(key), options);
            status = $"Microsoft Foundry {llm.FoundryResource} / {llm.Deployment} (API key)";
        }
        else
        {
            var tenant = llm.FoundryTenantId ?? throw new InvalidOperationException("Llm:FoundryTenantId is required for Entra authentication.");
#pragma warning disable OPENAI001 // AuthenticationPolicy constructor is marked experimental
            chat = new ChatClient(llm.Deployment, new OpenAiCompatibleProvider.TokenPolicy(new AzureCliTokenProvider(), tenant, "https://cognitiveservices.azure.com/.default"), options);
#pragma warning restore OPENAI001
            status = $"Microsoft Foundry {llm.FoundryResource} / {llm.Deployment} (Entra via Azure CLI)";
        }
        return new OpenAiCompatibleProvider(chat, llm, status);
    }

    private static IAnthropicClient? Create(AppSettings settings, SecretStore secrets, out string status)
    {
        var llm = settings.Llm;
        if (llm.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? secrets.Get("anthropic-key");
            status = key is null ? "Anthropic provider selected but no ANTHROPIC_API_KEY is set." : $"Anthropic API ({llm.Deployment})";
            return key is null ? null : new AnthropicClient { ApiKey = key };
        }

        if (string.IsNullOrWhiteSpace(llm.FoundryResource))
        {
            status = "Foundry resource not configured (Llm:FoundryResource).";
            return null;
        }
        if (llm.FoundryAuth.Equals("apikey", StringComparison.OrdinalIgnoreCase))
        {
            var key = Environment.GetEnvironmentVariable("DVQNA_FOUNDRY_KEY") ?? secrets.Get("foundry-key");
            if (key is null)
            {
                status = "Foundry API key not set. Run: dvqna set-foundry-key";
                return null;
            }
            status = $"Microsoft Foundry {llm.FoundryResource} / {llm.Deployment} (API key)";
            return new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(key, llm.FoundryResource));
        }
        var tenant = llm.FoundryTenantId ?? throw new InvalidOperationException("Llm:FoundryTenantId is required for Entra authentication.");
        status = $"Microsoft Foundry {llm.FoundryResource} / {llm.Deployment} (Entra via Azure CLI)";
        return new AnthropicFoundryClient(new AnthropicFoundryIdentityTokenCredentials(
            new ProviderCredential(new AzureCliTokenProvider(), tenant), llm.FoundryResource, ["https://ai.azure.com/.default"]));
    }

    private sealed class ProviderCredential(ITokenProvider tokens, string tenant) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct) => GetTokenAsync(ctx, ct).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct) =>
            new(await tokens.GetTokenAsync(tenant, ctx.Scopes, ct), DateTimeOffset.UtcNow.AddMinutes(30));
    }
}
