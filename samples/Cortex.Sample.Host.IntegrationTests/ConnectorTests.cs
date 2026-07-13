using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Application.Connectors;
using Cortex.Application.Files;
using Cortex.Connectors.LocalFolder;
using Cortex.Connectors.Sdk;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Cortex.Connectors;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The connector pipeline end to end (docs/PLATFORM_CONNECTORS_RAG_PLAN.md, phase 2), keyless via
/// the local-folder connector: connectors are default-OFF until a tenant admin enables and
/// configures them through /api/admin/connectors; enabled connectors contribute agent tools whose
/// fetch lands in the tenant file store; secrets are write-only through the admin API; disabling
/// removes the tools immediately; enablement is strictly per tenant.
/// </summary>
[Collection("api")]
public sealed class ConnectorTests(IntegrationFixture fixture) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cortex-connector-test").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void HostFilesystemConnector_requires_deployment_operator_enablement()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.AddCortexConnectors();
        using var services = builder.Services.BuildServiceProvider();

        Assert.DoesNotContain(
            services.GetServices<IConnector>(),
            connector => connector.Manifest.Id == LocalFolderConnector.ConnectorId);
    }

    [Fact]
    public async Task LocalFolder_refuses_a_tenant_root_outside_the_operator_allowlist()
    {
        using var admin = fixture.ClientFor("system_admin");
        (await admin.PutAsJsonAsync("/api/admin/connectors/local-folder/settings",
            new { values = new Dictionary<string, string?> { ["RootPath"] = AppContext.BaseDirectory } }))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/local-folder/enable", null)).EnsureSuccessStatusCode();

        try
        {
            using var scope = await UserScopeAsync("it-local-root-guard");
            Assert.Contains("not enabled", await Tools(scope).ListLocalFolder());
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/local-folder/disable", null);
        }
    }

    [Fact]
    public async Task Admin_enables_local_folder_and_agent_tools_fetch_into_the_file_store()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "nda-draft.txt"),
            "Mutual NDA draft. Confidential information must be protected for five years.");

        using var admin = fixture.ClientFor("system_admin");

        // Installed but default-OFF: both built-ins are listed, neither is enabled.
        var listing = await admin.GetFromJsonAsync<JsonElement>("/api/admin/connectors");
        var localFolder = listing.GetProperty("installed").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "local-folder");
        Assert.False(localFolder.GetProperty("enabled").GetBoolean());
        Assert.Contains(listing.GetProperty("installed").EnumerateArray(), c => c.GetProperty("id").GetString() == "azure-blob");

        // Before enablement the tenant has no connector tools and the tool answers honestly.
        using (var scope = await UserScopeAsync("it-user"))
        {
            Assert.Empty(await ToolCatalog(scope).GetEnabledToolsAsync(scope.ServiceProvider));
            Assert.Contains("not enabled", await Tools(scope).ListLocalFolder());
        }

        // Stage 1: the tenant admin configures + enables (schema-driven settings).
        (await admin.PutAsJsonAsync("/api/admin/connectors/local-folder/settings",
            new { values = new Dictionary<string, string?> { ["RootPath"] = _root } })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/local-folder/enable", null)).EnsureSuccessStatusCode();

        listing = await admin.GetFromJsonAsync<JsonElement>("/api/admin/connectors");
        localFolder = listing.GetProperty("installed").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "local-folder");
        Assert.True(localFolder.GetProperty("enabled").GetBoolean());
        Assert.True(localFolder.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "RootPath")
            .GetProperty("hasValue").GetBoolean());

        // The agent now gets the connector's tools, and fetch imports into the tenant file store.
        using (var scope = await UserScopeAsync("it-user"))
        {
            var tools = await ToolCatalog(scope).GetEnabledToolsAsync(scope.ServiceProvider);
            Assert.Equal(["fetch_from_local_folder", "list_local_folder"], tools.Select(t => t.Name).Order());

            Assert.Contains("nda-draft.txt", await Tools(scope).ListLocalFolder());

            var fetched = await Tools(scope).FetchFromLocalFolder("nda-draft.txt");
            Assert.Contains("File id:", fetched);
            var fileId = Guid.Parse(fetched.Split("File id:")[1].Split('.')[0].Trim());

            var stored = await scope.ServiceProvider.GetRequiredService<IFileStore>().FindAsync(fileId);
            Assert.NotNull(stored);
            Assert.Equal("connector:local-folder", stored!.Source);
            Assert.Equal("text/plain", stored.ContentType);

            // Containment: a traversal attempt reads as a missing file.
            Assert.Contains("No file named", await Tools(scope).FetchFromLocalFolder("../../secrets.txt"));
        }

        // Disabling removes the tools immediately; settings survive for a later re-enable.
        (await admin.PostAsync("/api/admin/connectors/local-folder/disable", null)).EnsureSuccessStatusCode();
        using (var scope = await UserScopeAsync("it-user"))
        {
            Assert.Empty(await ToolCatalog(scope).GetEnabledToolsAsync(scope.ServiceProvider));
            Assert.Contains("not enabled", await Tools(scope).ListLocalFolder());
        }
    }

    [Fact]
    public async Task Secret_settings_are_write_only_and_unprotected_only_server_side()
    {
        using var admin = fixture.ClientFor("system_admin");

        (await admin.PutAsJsonAsync("/api/admin/connectors/azure-blob/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["ConnectionString"] = "UseDevelopmentStorage=true;super-secret-key",
                ["Container"] = "legal-docs",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/azure-blob/enable", null)).EnsureSuccessStatusCode();

        // The admin read reports THAT a secret exists — never the secret itself.
        var response = await admin.GetAsync("/api/admin/connectors");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-key", body);

        var azure = JsonDocument.Parse(body).RootElement.GetProperty("installed").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "azure-blob");
        var connectionSetting = azure.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "ConnectionString");
        Assert.True(connectionSetting.GetProperty("isSecret").GetBoolean());
        Assert.True(connectionSetting.GetProperty("hasValue").GetBoolean());

        // ...and it is stored protected: the raw row must not contain the plaintext.
        using (var scope = await UserScopeAsync("it-user"))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var row = await db.TenantConnectors.FirstAsync(c => c.ConnectorId == "azure-blob");
            Assert.DoesNotContain("super-secret-key", row.SettingsJson);

            // Connector code, server-side, reads the real value through the settings seam.
            var values = await scope.ServiceProvider.GetRequiredService<IConnectorSettings>().GetAsync("azure-blob");
            Assert.Equal("UseDevelopmentStorage=true;super-secret-key", values!["ConnectionString"]);
            Assert.Equal("legal-docs", values["Container"]);
        }

        (await admin.PostAsync("/api/admin/connectors/azure-blob/disable", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Enablement_is_per_tenant_and_management_needs_the_admin_permission()
    {
        using var admin = fixture.ClientFor("system_admin");
        (await admin.PutAsJsonAsync("/api/admin/connectors/local-folder/settings",
            new { values = new Dictionary<string, string?> { ["RootPath"] = _root } })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/local-folder/enable", null)).EnsureSuccessStatusCode();

        // Another tenant sees the connector installed but NOT enabled — enablement never leaks.
        await fixture.EnsureTenantAsync("connector-tenant");
        using var foreignAdmin = fixture.ClientForTenant("system_admin", "connector-tenant");
        var listing = await foreignAdmin.GetFromJsonAsync<JsonElement>("/api/admin/connectors");
        var localFolder = listing.GetProperty("installed").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "local-folder");
        Assert.False(localFolder.GetProperty("enabled").GetBoolean());

        // A regular user may not manage integrations at all.
        using var user = fixture.ClientFor("user");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/connectors")).StatusCode);

        (await admin.PostAsync("/api/admin/connectors/local-folder/disable", null)).EnsureSuccessStatusCode();
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static IConnectorToolCatalog ToolCatalog(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IConnectorToolCatalog>();

    private static LocalFolderTools Tools(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LocalFolderTools>();

    /// <summary>A scope acting as a JIT-provisioned dev-tenant user with wildcard permissions.</summary>
    private async Task<IServiceScope> UserScopeAsync(string subject)
    {
        using (var warmup = fixture.Factory.CreateClient())
        {
            warmup.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
            warmup.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
            warmup.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Cortex.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}
