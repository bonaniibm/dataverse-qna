using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DvQna.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace DvQna.Auth;

/// <summary>
/// Delegated sign-in with the shared multi-tenant public client (design section 7.1).
/// One PublicClientApplication per tenant authority, all sharing one DPAPI-encrypted cache.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    private readonly string _clientId;
    private readonly string _cacheDir;
    private readonly ILogger<MsalTokenProvider> _log;
    private readonly ConcurrentDictionary<string, Lazy<Task<IPublicClientApplication>>> _apps = new();
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private MsalCacheHelper? _cacheHelper;

    public MsalTokenProvider(string clientId, string cacheDir, ILogger<MsalTokenProvider> log)
    {
        _clientId = clientId;
        _cacheDir = cacheDir;
        _log = log;
    }

    public bool UseBroker { get; init; }

    /// <summary>
    /// Requests named delegated scopes instead of ".default" so consent is asked for at sign-in
    /// (dynamic consent); the app registration then needs no pre-configured API permissions.
    /// </summary>
    internal static string[] ToDelegatedScopes(string[] scopes) => scopes.Select(s =>
    {
        if (!s.EndsWith("/.default", StringComparison.Ordinal)) return s;
        var resource = s[..^"/.default".Length];
        return resource.Contains("api.loganalytics.io", StringComparison.OrdinalIgnoreCase)
            ? resource + "/Data.Read"
            : resource.Contains("dynamics.com", StringComparison.OrdinalIgnoreCase) ? resource + "/user_impersonation" : s;
    }).ToArray();

    public async Task<string> GetTokenAsync(string tenantId, string[] scopes, CancellationToken ct)
    {
        scopes = ToDelegatedScopes(scopes);
        var app = await _apps.GetOrAdd(tenantId, t => new(() => CreateAppAsync(t))).Value;
        var accounts = (await app.GetAccountsAsync()).ToList();
        var account = accounts.FirstOrDefault(a => a.HomeAccountId?.TenantId == tenantId) ?? accounts.FirstOrDefault();
        if (account is not null)
        {
            try
            {
                var silent = await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
                return silent.AccessToken;
            }
            catch (MsalUiRequiredException) { }
        }

        await _interactive.WaitAsync(ct);
        try
        {
            _log.LogInformation("Interactive sign-in for tenant {Tenant}", tenantId);
            var builder = app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount);
            if (UseBroker)
            {
                var hwnd = NativeMethods.GetConsoleOrTerminalWindow();
                if (hwnd != IntPtr.Zero) builder = builder.WithParentActivityOrWindow(hwnd);
            }
            else
            {
                builder = builder.WithUseEmbeddedWebView(false);
            }
            var result = await builder.ExecuteAsync(ct);
            return result.AccessToken;
        }
        finally
        {
            _interactive.Release();
        }
    }

    private async Task<IPublicClientApplication> CreateAppAsync(string tenantId)
    {
        var builder = PublicClientApplicationBuilder.Create(_clientId)
            .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
            .WithDefaultRedirectUri()
            .WithClientName("Dataverse QnA");
        if (UseBroker && OperatingSystem.IsWindows() && NativeMethods.GetConsoleOrTerminalWindow() != IntPtr.Zero)
        {
            builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Dataverse QnA" });
        }
        else
        {
            builder = builder.WithRedirectUri("http://localhost");
        }
        var app = builder.Build();

        if (_cacheHelper is null)
        {
            Directory.CreateDirectory(_cacheDir);
            var props = new StorageCreationPropertiesBuilder("msal_cache.bin", _cacheDir).Build();
            _cacheHelper = await MsalCacheHelper.CreateAsync(props);
        }
        _cacheHelper.RegisterCache(app.UserTokenCache);
        return app;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        public static IntPtr GetConsoleOrTerminalWindow()
        {
            if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
            var console = GetConsoleWindow();
            return console == IntPtr.Zero ? IntPtr.Zero : GetAncestor(console, 3 /* GA_ROOTOWNER */);
        }
    }
}

/// <summary>
/// Developer fallback: borrows tokens from the signed-in Azure CLI. Not for distribution builds.
/// </summary>
public sealed class AzureCliTokenProvider : ITokenProvider
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();

    public async Task<string> GetTokenAsync(string tenantId, string[] scopes, CancellationToken ct)
    {
        var scope = scopes[0];
        var key = tenantId + "|" + scope;
        if (_cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow.AddMinutes(5))
            return hit.Token;

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "az",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add("az"); }
        foreach (var a in new[] { "account", "get-access-token", "--tenant", tenantId, "--scope", scope, "-o", "json" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Azure CLI");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"Azure CLI token request failed for tenant {tenantId}: {stderr.Trim()}");

        using var doc = JsonDocument.Parse(stdout);
        var token = doc.RootElement.GetProperty("accessToken").GetString()!;
        var expires = doc.RootElement.TryGetProperty("expires_on", out var eo) && eo.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(eo.GetInt64())
            : DateTimeOffset.UtcNow.AddMinutes(30);
        _cache[key] = (token, expires);
        return token;
    }
}
