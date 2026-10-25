using System.Net;
using System.Reflection;
using System.Text.Json;
using DvQna.Agent;
using DvQna.Connectors;
using DvQna.Core;

namespace DvQna.Tests;

/// <summary>Read-only guarantees (design section 11.4) and privacy shaping (11.3).</summary>
public class ReadOnlyTests
{
    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Http_guard_blocks_every_method_except_get(string method)
    {
        using var client = new HttpClient(new DataverseReader.ReadOnlyHandler(new OkHandler()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "https://org.crm.dynamics.com/api/data/v9.2/accounts")));
    }

    [Fact]
    public async Task Http_guard_allows_get()
    {
        using var client = new HttpClient(new DataverseReader.ReadOnlyHandler(new OkHandler()));
        var resp = await client.GetAsync("https://org.crm.dynamics.com/api/data/v9.2/accounts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed class NoTokens : ITokenProvider
    {
        public Task<string> GetTokenAsync(string tenantId, string[] scopes, CancellationToken ct) => throw new InvalidOperationException("should not be called");
    }

    [Theory]
    [InlineData("ExecuteWorkflow(EntityId=1)")]
    [InlineData("systemusers(00000000-0000-0000-0000-000000000000)/Microsoft.Dynamics.CRM.AddPrivilegesRole()")]
    [InlineData("BulkDelete()")]
    public async Task Functions_outside_the_allow_list_are_rejected(string call)
    {
        var reader = new DataverseReader(new NoTokens(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DataverseReader>.Instance);
        var env = new EnvironmentProfile { Name = "t", Url = "https://org.crm.dynamics.com", TenantId = "x" };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.FunctionAsync(env, call, CancellationToken.None));
        Assert.Contains("allow-list", ex.Message);
    }

    [Fact]
    public void Dataverse_reader_interface_exposes_no_write_methods()
    {
        var names = typeof(IDataverseReader).GetMethods().Select(m => m.Name).ToList();
        string[] forbidden = ["Create", "Update", "Delete", "Upsert", "Associate", "Disassociate", "Execute", "Post", "Patch"];
        Assert.DoesNotContain(names, n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Tools_assembly_has_no_write_capable_types()
    {
        // Tools must only depend on the read-only reader, never on HttpClient directly.
        var toolTypes = typeof(DvQna.Tools.Tool).Assembly.GetTypes();
        foreach (var t in toolTypes)
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
                Assert.NotEqual(typeof(HttpClient), f.FieldType);
    }
}

public class FlattenTests
{
    [Fact]
    public void Formatted_values_become_label_columns_and_odata_noise_is_dropped()
    {
        using var doc = JsonDocument.Parse("""
            {"@odata.etag":"W/1","statuscode":31,"statuscode@OData.Community.Display.V1.FormattedValue":"Failed",
             "_ownerid_value":"abc","_ownerid_value@Microsoft.Dynamics.CRM.lookuplogicalname":"systemuser","name":"Job"}
            """);
        var row = DataverseReader.Flatten(doc.RootElement);
        Assert.Equal(31L, row["statuscode"]);
        Assert.Equal("Failed", row["statuscode@label"]);
        Assert.Equal("Job", row["name"]);
        Assert.False(row.ContainsKey("@odata.etag"));
        Assert.DoesNotContain(row.Keys, k => k.Contains("lookuplogicalname"));
    }
}

public class RedactorTests
{
    [Fact]
    public void Operational_mode_redacts_emails_and_secrets_but_keeps_timestamps()
    {
        var s = Redactor.Clean("Failed for jane.doe@contoso.com at 2026-10-24 09:00:00; Password=hunter2; Bearer eyJabc.def", PrivacyMode.Operational, "error")!;
        Assert.DoesNotContain("jane.doe", s);
        Assert.DoesNotContain("hunter2", s);
        Assert.DoesNotContain("eyJabc", s);
        Assert.Contains("2026-10-24 09:00:00", s);
    }

    [Fact]
    public void Metadata_mode_hides_free_text_columns()
    {
        Assert.StartsWith("[hidden", Redactor.Clean("Customer ACME failed", PrivacyMode.Metadata, "error"));
        Assert.Equal("Update", Redactor.Clean("Update", PrivacyMode.Metadata, "message_name"));
    }

    [Fact]
    public void Full_mode_passes_through() => Assert.Equal("a@b.com", Redactor.Clean("a@b.com", PrivacyMode.Full, "error"));
}
