using System.ComponentModel;
using System.Text;
using Plenipo.Application.Authorization;
using Plenipo.Application.Connectors;
using Plenipo.Application.Files;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.MsGraph;

/// <summary>
/// The Microsoft 365 tools. Every call rides the CURRENT USER's delegated token — Graph enforces
/// their own OneDrive/SharePoint permissions per request, so there is nothing to trim on our
/// side (the federated lane). Not connected yet → the tool answers with the connect link.
/// </summary>
public sealed class MsGraphTools(IConnectorUserLogins logins, IGraphApiClient graph, IFileStore files)
{
    private const string NotConnected =
        "Your Microsoft 365 account is not connected (or the session expired). " +
        "Open /api/connectors/msgraph/oauth/start to connect it, then try again.";

    [Description("List files in YOUR connected OneDrive/SharePoint (optionally under a folder path like 'Contracts/2026'). Requires your Microsoft account to be connected.")]
    public async Task<string> ListM365Files(
        [Description("Optional folder path under the drive root.")] string? folderPath = null,
        CancellationToken cancellationToken = default)
    {
        var token = await logins.GetAccessTokenAsync(MsGraphConnector.ConnectorId, cancellationToken);
        if (token is null)
        {
            return NotConnected;
        }

        var items = await graph.ListDriveItemsAsync(token, folderPath, cancellationToken);
        if (items.Count == 0)
        {
            return folderPath is null ? "Your drive root is empty." : $"No items under '{folderPath}'.";
        }

        var sb = new StringBuilder("Files in your Microsoft 365 drive:\n");
        foreach (var item in items.Take(100))
        {
            sb.AppendLine(item.IsFolder
                ? $"- {item.Name}/ (folder)"
                : $"- {item.Name} ({item.Size:N0} bytes, id: {item.Id})");
        }

        return sb.ToString();
    }

    [Description("Copy a file from YOUR OneDrive/SharePoint into the tenant file store and return its file id (usable with read_document, attach tools, and indexing).")]
    public async Task<string> FetchFromM365(
        [Description("The drive item id, as shown by list_m365_files.")] string itemId,
        [Description("The file name to store it under.")] string fileName,
        CancellationToken cancellationToken = default)
    {
        var token = await logins.GetAccessTokenAsync(MsGraphConnector.ConnectorId, cancellationToken);
        if (token is null)
        {
            return NotConnected;
        }

        var download = await graph.DownloadAsync(token, itemId, cancellationToken);
        if (download is null)
        {
            return $"No drive item '{itemId}' exists (or you lack access to it). Use list_m365_files to see what is available.";
        }

        await using var content = download.Content;
        var stored = await files.SaveAsync(
            fileName, download.ContentType, content,
            source: $"connector:{MsGraphConnector.ConnectorId}", cancellationToken);

        return $"Imported '{stored.FileName}' ({stored.SizeBytes:N0} bytes) from Microsoft 365. File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }
}

/// <summary>Supplies the Microsoft 365 connector's executable tools.</summary>
public sealed class MsGraphToolSource : IConnectorToolSource
{
    public string ConnectorId => MsGraphConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<MsGraphTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_m365_files",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_m365_files"),
                Function = AIFunctionFactory.Create(tools.ListM365Files, name: "list_m365_files"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_from_m365",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_m365"),
                Function = AIFunctionFactory.Create(tools.FetchFromM365, name: "fetch_from_m365"),
                RequiresApproval = true,
            },
        ];
    }
}
