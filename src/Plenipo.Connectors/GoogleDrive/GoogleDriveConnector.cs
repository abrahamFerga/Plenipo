using Plenipo.Application.Authorization;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.GoogleDrive;

/// <summary>
/// Google Drive as a delegated data source: each user connects THEIR OWN Google account (auth
/// code + PKCE against Google's fixed OAuth endpoints — no per-tenant authority), and every tool
/// call rides their token so Google enforces their permissions. Second proof of the delegated
/// lane after Microsoft 365, and the first with a non-Entra URL shape.
/// </summary>
public sealed class GoogleDriveConnector : IConnector
{
    public const string ConnectorId = "google-drive";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Google Drive",
        Description = "Browse and fetch the user's Google Drive documents. Each user connects their own Google account; Google enforces their permissions per call.",
        AuthMode = ConnectorAuthMode.UserDelegated,
        Icon = "folder",
        // Google's OAuth endpoints are fixed URLs (no tenant authority). access_type=offline +
        // prompt=consent make Google return a refresh token on first consent.
        OAuthAuthorizeUrlTemplate = "https://accounts.google.com/o/oauth2/v2/auth?access_type=offline&prompt=consent",
        OAuthTokenUrlTemplate = "https://oauth2.googleapis.com/token",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "ClientId",
                Label = "Client id",
                Description = "The Google Cloud OAuth client id (Web application type).",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ClientSecret",
                Label = "Client secret",
                Description = "The OAuth client secret. Stored protected; never shown again.",
                IsSecret = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "Scopes",
                Label = "Scopes",
                Description = "Delegated scopes (default: https://www.googleapis.com/auth/drive.readonly).",
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_gdrive_files",
                Description = "List files in the user's connected Google Drive.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_gdrive_files"),
            },
            new ToolDescriptor
            {
                Name = "fetch_from_gdrive",
                Description = "Copy a Drive file into the tenant file store.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_gdrive"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(DriveApiClient.HttpClientName);
        services.AddSingleton<IDriveApiClient, DriveApiClient>();
        services.AddScoped<GoogleDriveTools>();
        services.AddSingleton<IConnectorToolSource, GoogleDriveToolSource>();
    }
}
