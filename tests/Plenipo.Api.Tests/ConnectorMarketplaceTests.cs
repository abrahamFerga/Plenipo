using System.Text.Json;
using Plenipo.Connectors;
using Plenipo.Connectors.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The in-box connector marketplace: one call registers every bundled connector, configuration
/// (not code) narrows what a deployment offers, and the admin catalog shows what's installed
/// alongside what exists in the ecosystem but isn't — with the package an operator would add.
/// </summary>
public sealed class ConnectorMarketplaceTests
{
    private static readonly string[] BundledIds =
        ["local-folder", "azure-blob", "s3", "msgraph", "google-drive", "documenso", "plenipo-peer"];

    [Fact]
    public void AddPlenipoConnectors_registers_every_bundled_connector()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connectors:OperatorEnabled:local-folder"] = "true",
        });

        builder.AddPlenipoConnectors();

        var registered = RegisteredConnectorIds(builder.Services);
        Assert.Equal(BundledIds.OrderBy(x => x), registered.OrderBy(x => x));
    }

    [Fact]
    public void Connectors_Exclude_suppresses_a_bundled_connector_by_config_alone()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Connectors:Exclude:0"] = "s3",
            ["Connectors:Exclude:1"] = "Documenso", // case-insensitive on purpose
            ["Connectors:OperatorEnabled:local-folder"] = "true",
        });

        builder.AddPlenipoConnectors();

        var registered = RegisteredConnectorIds(builder.Services);
        Assert.DoesNotContain("s3", registered);
        Assert.DoesNotContain("documenso", registered);
        Assert.Contains("local-folder", registered);
    }

    [Fact]
    public async Task Admin_catalog_splits_installed_from_available_when_config_excludes_one()
    {
        // The bare platform host registers the whole bundle; excluding documenso by config must
        // move it from "installed" to "available" — including the package an operator would add.
        using var factory = new ExcludeDocumensoFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "marketplace-admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.GetAsync("/api/admin/connectors");
        response.EnsureSuccessStatusCode();
        var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var installedIds = catalog.GetProperty("installed").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.DoesNotContain("documenso", installedIds);
        Assert.Contains("local-folder", installedIds);

        var available = catalog.GetProperty("available").EnumerateArray().ToList();
        var documenso = Assert.Single(available);
        Assert.Equal("documenso", documenso.GetProperty("id").GetString());
        Assert.Equal("Plenipo.Connectors", documenso.GetProperty("package").GetString());
        Assert.False(string.IsNullOrWhiteSpace(documenso.GetProperty("registration").GetString()));
    }

    [Fact]
    public async Task Admin_catalog_reports_nothing_available_when_the_whole_bundle_is_installed()
    {
        using var factory = new PlenipoApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "marketplace-admin-2");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.GetAsync("/api/admin/connectors");
        response.EnsureSuccessStatusCode();
        var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var installedIds = catalog.GetProperty("installed").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();
        foreach (var id in BundledIds)
        {
            Assert.Contains(id, installedIds);
        }

        Assert.Empty(catalog.GetProperty("available").EnumerateArray());
    }

    private static List<string> RegisteredConnectorIds(IServiceCollection services) =>
        services
            .Where(d => d.ServiceType == typeof(IConnector))
            .Select(d => ((IConnector)d.ImplementationInstance!).Manifest.Id)
            .ToList();

    private sealed class ExcludeDocumensoFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting flows into IConfiguration before Program.cs registers the bundle.
            builder.UseSetting("Connectors:Exclude:0", "documenso");
            base.ConfigureWebHost(builder);
        }
    }
}
