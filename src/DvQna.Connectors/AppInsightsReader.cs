using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using DvQna.Core;

namespace DvQna.Connectors;

/// <summary>Queries the App Insights resource linked to an environment, with the user's Entra token.</summary>
public sealed class AppInsightsReader(ITokenProvider tokens)
{
    public const string Scope = "https://api.loganalytics.io/.default";

    public async Task<List<Dictionary<string, object?>>> QueryAsync(EnvironmentProfile env, string kql, TimeSpan window, int maxRows, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(env.AppInsightsResourceId))
            throw new InvalidOperationException($"No App Insights resource is configured for {env.Name}.");
        var tenant = env.AppInsightsTenantId ?? env.TenantId;
        var client = new LogsQueryClient(new ProviderCredential(tokens, tenant));
        var response = await client.QueryResourceAsync(new ResourceIdentifier(env.AppInsightsResourceId), kql, new QueryTimeRange(window), cancellationToken: ct);
        var table = response.Value.Table;
        var rows = new List<Dictionary<string, object?>>();
        foreach (LogsTableRow r in table.Rows.Take(maxRows))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < table.Columns.Count; i++) row[table.Columns[i].Name] = r[i]?.ToString();
            rows.Add(row);
        }
        return rows;
    }

    private sealed class ProviderCredential(ITokenProvider tokens, string tenant) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext ctx, CancellationToken ct) => GetTokenAsync(ctx, ct).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext ctx, CancellationToken ct)
        {
            var token = await tokens.GetTokenAsync(tenant, [Scope], ct);
            return new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(30));
        }
    }
}
