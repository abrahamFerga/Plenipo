using System.Net.Http.Json;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Sample.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// A connector defined in the PRODUCT HOST's own assembly (<see cref="HostDefinedCrmConnector"/> —
/// not shipped in the Plenipo.Connectors package) is a first-class connector: it appears in the
/// catalog, is enabled + configured per tenant through the generic admin endpoints, and its tools
/// resolve the tenant's protected settings and run — all identical to a built-in. This is the seam
/// a domain system (e.g. Networthy owning a Plaid connector in its own repo) relies on.
/// </summary>
[Collection("api")]
public sealed class HostDefinedConnectorTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task HostDefinedConnector_IsCataloged_Configured_AndItsToolsRun()
    {
        var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        admin.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        // The catalog surfaced a connector defined outside the Plenipo.Connectors package.
        var listing = await admin.GetFromJsonAsync<ConnectorListing>("/api/admin/connectors/");
        Assert.NotNull(listing);
        Assert.Contains(listing!.Installed, c => c.Id == HostDefinedCrmConnector.ConnectorId);

        (await admin.PutAsJsonAsync($"/api/admin/connectors/{HostDefinedCrmConnector.ConnectorId}/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["BaseUrl"] = "https://crm.example.test",
                ["ApiKey"] = "sk-fake-crm-key",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/admin/connectors/{HostDefinedCrmConnector.ConnectorId}/enable", null))
            .EnsureSuccessStatusCode();

        try
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
            context.SetTenant(tenant.Id);
            context.SetPermissions(["*"]);

            var tools = scope.ServiceProvider.GetRequiredService<HostDefinedCrmTools>();

            // The tool resolved the TENANT's protected settings (secret write-only, still usable in code).
            var answer = await tools.LookupContact("ada@example.test");
            Assert.Contains("ada@example.test", answer);
            Assert.Contains("https://crm.example.test", answer);
            Assert.Contains("resolved from the tenant's settings", answer);
        }
        finally
        {
            await admin.PostAsync($"/api/admin/connectors/{HostDefinedCrmConnector.ConnectorId}/disable", null);
        }
    }

    [Fact]
    public async Task HostDefinedConnector_Unconfigured_AnswersWithGuidance()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        context.SetPermissions(["*"]);

        var tools = scope.ServiceProvider.GetRequiredService<HostDefinedCrmTools>();
        Assert.Contains("not enabled for this tenant", await tools.LookupContact("x@example.test"));
    }

    private sealed record ConnectorListing(List<ConnectorRow> Installed);

    private sealed record ConnectorRow(string Id);
}
