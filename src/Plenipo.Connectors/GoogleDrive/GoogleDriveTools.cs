using System.ComponentModel;
using System.Text;
using Plenipo.Application.Authorization;
using Plenipo.Application.Connectors;
using Plenipo.Application.Files;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.GoogleDrive;

/// <summary>
/// The Google Drive tools. Every call rides the CURRENT USER's delegated token — Google enforces
/// their own Drive permissions per request, so there is nothing to trim on our side. Not
/// connected yet → the tool answers with the connect link.
/// </summary>
public sealed class GoogleDriveTools(IConnectorUserLogins logins, IDriveApiClient drive, IFileStore files)
{
    private const string NotConnected =
        "Your Google account is not connected (or the session expired). " +
        "Open /api/connectors/google-drive/oauth/start to connect it, then try again.";

    [Description("List files in YOUR connected Google Drive (optionally filtered by a name fragment). Requires your Google account to be connected.")]
    public async Task<string> ListGoogleDriveFiles(
        [Description("Optional name filter (files whose name contains this).")] string? nameContains = null,
        CancellationToken cancellationToken = default)
    {
        var token = await logins.GetAccessTokenAsync(GoogleDriveConnector.ConnectorId, cancellationToken);
        if (token is null)
        {
            return NotConnected;
        }

        var items = await drive.ListFilesAsync(token, nameContains, cancellationToken);
        if (items.Count == 0)
        {
            return nameContains is null ? "Your Drive shows no files." : $"No files match '{nameContains}'.";
        }

        var sb = new StringBuilder("Files in your Google Drive:\n");
        foreach (var item in items.Take(100))
        {
            sb.AppendLine(item.IsFolder
                ? $"- {item.Name}/ (folder)"
                : $"- {item.Name} ({item.Size:N0} bytes, id: {item.Id})");
        }

        return sb.ToString();
    }

    [Description("Copy a file from YOUR Google Drive into the tenant file store and return its file id (usable with read_document, attach tools, and indexing).")]
    public async Task<string> FetchFromGoogleDrive(
        [Description("The Drive file id, as shown by list_gdrive_files.")] string fileId,
        [Description("The file name to store it under.")] string fileName,
        CancellationToken cancellationToken = default)
    {
        var token = await logins.GetAccessTokenAsync(GoogleDriveConnector.ConnectorId, cancellationToken);
        if (token is null)
        {
            return NotConnected;
        }

        var download = await drive.DownloadAsync(token, fileId, cancellationToken);
        if (download is null)
        {
            return $"No Drive file '{fileId}' exists (or you lack access to it). Use list_gdrive_files to see what is available.";
        }

        await using var content = download.Content;
        var stored = await files.SaveAsync(
            fileName, download.ContentType, content,
            source: $"connector:{GoogleDriveConnector.ConnectorId}", cancellationToken);

        return $"Imported '{stored.FileName}' ({stored.SizeBytes:N0} bytes) from Google Drive. File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }
}

/// <summary>Supplies the Google Drive connector's executable tools.</summary>
public sealed class GoogleDriveToolSource : IConnectorToolSource
{
    public string ConnectorId => GoogleDriveConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<GoogleDriveTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_gdrive_files",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_gdrive_files"),
                Function = AIFunctionFactory.Create(tools.ListGoogleDriveFiles, name: "list_gdrive_files"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_from_gdrive",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_gdrive"),
                Function = AIFunctionFactory.Create(tools.FetchFromGoogleDrive, name: "fetch_from_gdrive"),
                RequiresApproval = true, // writes into the tenant file store
            },
        ];
    }
}
