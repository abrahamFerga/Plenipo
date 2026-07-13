using System.ComponentModel;
using System.Text;
using Azure.Storage.Blobs;
using Plenipo.Application.Authorization;
using Plenipo.Application.Files;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.AzureBlob;

/// <summary>
/// The azure-blob connector's tools. The connection string comes from the tenant's protected
/// settings per call — no cached clients across tenants — and fetch copies into the tenant file
/// store so every downstream capability works on the imported file unchanged.
/// </summary>
public sealed class AzureBlobTools(IConnectorSettings settings, IFileStore files)
{
    private const string NotConfigured =
        "The Azure Blob connector is not enabled for this tenant (or is missing its connection settings). " +
        "An admin can configure it under Integrations.";

    [Description("List blobs in the connected Azure Storage container (name and size), optionally filtered by a path prefix. Use fetch_from_azure_blob to import one.")]
    public async Task<string> ListAzureBlobs(
        [Description("Optional path prefix to filter by, e.g. 'contracts/2026/'.")] string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var container = await ResolveContainerAsync(cancellationToken);
        if (container is null)
        {
            return NotConfigured;
        }

        var sb = new StringBuilder("Blobs in the connected container:\n");
        var count = 0;
        await foreach (var blob in container.GetBlobsAsync(
            Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None,
            prefix, cancellationToken))
        {
            sb.AppendLine($"- {blob.Name} ({blob.Properties.ContentLength:N0} bytes)");
            if (++count >= 100)
            {
                sb.AppendLine("… (list capped at 100 — narrow with a prefix)");
                break;
            }
        }

        return count == 0
            ? prefix is null ? "The connected container is empty." : $"No blobs match prefix '{prefix}'."
            : sb.ToString();
    }

    [Description("Copy a blob from the connected Azure Storage container into the tenant file store and return its file id (usable with read_document, attach tools, and indexing).")]
    public async Task<string> FetchFromAzureBlob(
        [Description("The blob name (full path), as shown by list_azure_blobs.")] string blobName,
        CancellationToken cancellationToken = default)
    {
        var container = await ResolveContainerAsync(cancellationToken);
        if (container is null)
        {
            return NotConfigured;
        }

        var blob = container.GetBlobClient(blobName);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return $"No blob named '{blobName}' exists in the connected container. Use list_azure_blobs to see what is available.";
        }

        var download = await blob.DownloadContentAsync(cancellationToken);
        var contentType = download.Value.Details.ContentType;
        using var stream = download.Value.Content.ToStream();
        var stored = await files.SaveAsync(
            Path.GetFileName(blobName),
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            stream,
            source: $"connector:{AzureBlobConnector.ConnectorId}",
            cancellationToken);

        return $"Imported '{stored.FileName}' ({stored.SizeBytes:N0} bytes) from Azure Blob Storage. File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    private async Task<BlobContainerClient?> ResolveContainerAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(AzureBlobConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue(AzureBlobConnector.ConnectionStringSetting, out var connectionString) ||
            !values.TryGetValue(AzureBlobConnector.ContainerSetting, out var containerName) ||
            string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(containerName))
        {
            return null;
        }

        return new BlobContainerClient(connectionString, containerName);
    }
}

/// <summary>Supplies the azure-blob connector's executable tools.</summary>
public sealed class AzureBlobToolSource : IConnectorToolSource
{
    public string ConnectorId => AzureBlobConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<AzureBlobTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_azure_blobs",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_azure_blobs"),
                Function = AIFunctionFactory.Create(tools.ListAzureBlobs, name: "list_azure_blobs"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_from_azure_blob",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_azure_blob"),
                Function = AIFunctionFactory.Create(tools.FetchFromAzureBlob, name: "fetch_from_azure_blob"),
                RequiresApproval = true,
            },
        ];
    }
}
