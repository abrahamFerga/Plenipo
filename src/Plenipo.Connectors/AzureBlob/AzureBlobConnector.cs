using Plenipo.Application.Authorization;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.AzureBlob;

/// <summary>
/// Service-auth connector to an Azure Blob Storage container — "the lawyer shop keeps their data
/// in blob storage" scenario. The tenant admin configures the connection string (secret,
/// write-only, protected at rest) and container; agents can then browse and, with approval, import
/// blobs into the tenant file store.
/// </summary>
public sealed class AzureBlobConnector : IConnector
{
    public const string ConnectorId = "azure-blob";

    public const string ConnectionStringSetting = "ConnectionString";
    public const string ContainerSetting = "Container";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Azure Blob Storage",
        Description = "Browse and fetch documents from an Azure Blob Storage container the tenant already uses.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "cloud",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = ConnectionStringSetting,
                Label = "Connection string",
                Description = "Storage-account connection string. Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = ContainerSetting,
                Label = "Container",
                Description = "The blob container to browse.",
                Required = true,
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_azure_blobs",
                Description = "List blobs in the connected Azure Storage container, optionally under a prefix.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_azure_blobs"),
            },
            new ToolDescriptor
            {
                Name = "fetch_from_azure_blob",
                Description = "Copy a blob from the connected container into the tenant file store. Side-effecting: imports external data, requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_azure_blob"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AzureBlobTools>();
        services.AddSingleton<IConnectorToolSource, AzureBlobToolSource>();
    }
}
