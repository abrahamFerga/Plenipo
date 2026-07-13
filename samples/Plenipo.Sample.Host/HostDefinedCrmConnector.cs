using System.ComponentModel;
using Plenipo.Application.Authorization;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host;

/// <summary>
/// A connector defined by the PRODUCT HOST itself — not shipped in the Plenipo.Connectors package.
/// It exists to prove (and document by example) that a domain system can add its OWN connector:
/// reference Plenipo.Connectors.Sdk + Plenipo.Modules.Sdk, implement <see cref="IConnector"/> in your
/// own assembly, and register it with <c>builder.AddPlenipoConnector&lt;T&gt;()</c> exactly like a
/// built-in. The platform's catalog, per-tenant enablement, schema-driven settings (secrets
/// write-only), permission gating, and agent-tool exposure all work identically — nothing about
/// them is keyed to the connector's assembly. This is the seam a product like Networthy uses to
/// own a domain-specific connector (e.g. Plaid) in its own repo.
///
/// Service-auth with one secret; the single read tool resolves the tenant's settings and answers
/// from them, so the whole path is exercised with no external dependency and no fakes.
/// </summary>
public sealed class HostDefinedCrmConnector : IConnector
{
    public const string ConnectorId = "sample-crm";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Sample CRM (host-defined)",
        Description = "Illustrates a product-defined connector living in the host's own assembly, not the Plenipo.Connectors package.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "users",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "BaseUrl",
                Label = "CRM base URL",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ApiKey",
                Label = "API key",
                Description = "Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "lookup_contact",
                Description = "Look up a CRM contact by email on the connected instance.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "lookup_contact"),
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<HostDefinedCrmTools>();
        services.AddSingleton<IConnectorToolSource, HostDefinedCrmToolSource>();
    }
}

/// <summary>The host-defined connector's tools. Resolves the tenant's protected settings per call,
/// exactly like the built-in service-auth connectors (S3, Documenso).</summary>
public sealed class HostDefinedCrmTools(IConnectorSettings settings)
{
    private const string NotConfigured =
        "The sample CRM connector is not enabled for this tenant (or is missing its base URL / API key). " +
        "An admin can configure it under Integrations.";

    [Description("Look up a CRM contact by email address on the connected instance.")]
    public async Task<string> LookupContact(
        [Description("The contact's email address.")] string email,
        CancellationToken cancellationToken = default)
    {
        var values = await settings.GetAsync(HostDefinedCrmConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue("BaseUrl", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl) ||
            !values.TryGetValue("ApiKey", out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            return NotConfigured;
        }

        // A real connector would call baseUrl with apiKey here; the sample answers from the
        // resolved connection so the full settings->tool path is testable with no network.
        return $"Looked up '{email}' on the CRM at {baseUrl} (connection resolved from the tenant's settings).";
    }
}

/// <summary>Supplies the host-defined connector's executable tools.</summary>
public sealed class HostDefinedCrmToolSource : IConnectorToolSource
{
    public string ConnectorId => HostDefinedCrmConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<HostDefinedCrmTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "lookup_contact",
                Permission = Permissions.ForConnectorTool(ConnectorId, "lookup_contact"),
                Function = AIFunctionFactory.Create(tools.LookupContact, name: "lookup_contact"),
            },
        ];
    }
}
