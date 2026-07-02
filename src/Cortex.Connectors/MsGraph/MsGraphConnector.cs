using Cortex.Application.Authorization;
using Cortex.Connectors.Sdk;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Connectors.MsGraph;

/// <summary>
/// The Microsoft 365 connector (OneDrive/SharePoint documents via Microsoft Graph) — the named
/// customer scenario: "the lawyer shop keeps their data in Microsoft 365". Delegated auth: the
/// admin registers the Entra app (stage 1: Authority/ClientId/Scopes here), then EACH USER
/// connects their own account (stage 2: /api/connectors/msgraph/oauth/start), so Graph enforces
/// the user's own SharePoint/OneDrive permissions on every call — the two-lane model's federated
/// lane, exactly like Harvey's iManage picker. Disabling the connector revokes every session.
/// </summary>
public sealed class MsGraphConnector : IConnector
{
    public const string ConnectorId = "msgraph";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Microsoft 365 (OneDrive/SharePoint)",
        Description = "Browse and fetch the user's OneDrive/SharePoint documents via Microsoft Graph. Each user connects their own account; Microsoft enforces their permissions per call.",
        AuthMode = ConnectorAuthMode.UserDelegated,
        Icon = "briefcase",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "Authority",
                Label = "Authority",
                Description = "Entra authority, e.g. https://login.microsoftonline.com/<tenant-id>",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ClientId",
                Label = "Client id",
                Description = "The Entra app registration's application (client) id.",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ClientSecret",
                Label = "Client secret",
                Description = "Confidential-client secret. Stored protected; never shown again.",
                IsSecret = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "Scopes",
                Label = "Scopes",
                Description = "Delegated scopes (default: offline_access Files.Read.All).",
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_m365_files",
                Description = "List files in the user's connected OneDrive/SharePoint (optionally under a folder path).",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_m365_files"),
            },
            new ToolDescriptor
            {
                Name = "fetch_from_m365",
                Description = "Copy a file from the user's OneDrive/SharePoint into the tenant file store. Side-effecting: imports external data, requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_m365"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(GraphApiClient.HttpClientName);
        services.AddSingleton<IGraphApiClient, GraphApiClient>();
        services.AddScoped<MsGraphTools>();
        services.AddSingleton<IConnectorToolSource, MsGraphToolSource>();
    }
}
