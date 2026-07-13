using Cortex.Application.Authorization;
using Cortex.Connectors.Sdk;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Connectors.LocalFolder;

/// <summary>
/// The keyless dev/test connector: bridges a directory on the host into chat. It exists so every
/// deployment — and CI — can exercise the whole connector pipeline (enablement, settings, tools,
/// approval-gated fetch into the file store) without any external credentials, exactly like the
/// Mock chat provider and Mock embedder. Real deployments use it for watched folders (scanner
/// drops, exports).
/// </summary>
public sealed class LocalFolderConnector : IConnector
{
    public const string ConnectorId = "local-folder";

    /// <summary>The admin-configured directory the connector is allowed to read. Nothing outside it is reachable.</summary>
    public const string RootPathSetting = "RootPath";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Local folder",
        Description = "Browse, fetch, and sync files from a directory on the host (dev/test, watched folders). Only the configured root is reachable.",
        AuthMode = ConnectorAuthMode.Service,
        SupportsSync = true,
        RequiresOperatorEnablement = true,
        Icon = "folder-open",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = RootPathSetting,
                Label = "Root path",
                Description = "Absolute directory path the connector may read. Files outside it are unreachable.",
                Required = true,
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_local_folder",
                Description = "List the files available in the connected local folder.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_local_folder"),
            },
            new ToolDescriptor
            {
                Name = "fetch_from_local_folder",
                Description = "Copy a file from the connected local folder into the tenant file store. Side-effecting: imports external data, requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_local_folder"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalFolderOptions>(configuration.GetSection(LocalFolderOptions.SectionName));
        services.AddScoped<LocalFolderTools>();
        services.AddSingleton<IConnectorToolSource, LocalFolderToolSource>();
        services.AddScoped<IConnectorSyncSource, LocalFolderSyncSource>();
    }
}

/// <summary>Deployment-operator allowlist for host directories tenants may connect to.</summary>
public sealed class LocalFolderOptions
{
    public const string SectionName = "Connectors:LocalFolder";

    /// <summary>Absolute roots beneath which tenant-selected folders may live. Empty denies all.</summary>
    public string[] AllowedRoots { get; set; } = [];
}
