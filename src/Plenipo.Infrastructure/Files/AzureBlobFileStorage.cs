using Azure.Storage.Blobs;
using Plenipo.Application.Files;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Files;

/// <summary>
/// Azure Blob Storage <see cref="IFileBlobStorage"/> for production. One container, blobs keyed
/// <c>{tenantId}/{fileId}</c>. The container is created on first use so provisioning stays minimal.
/// </summary>
public sealed class AzureBlobFileStorage(IOptions<FileStorageOptions> options) : IFileBlobStorage
{
    private readonly Lazy<BlobContainerClient> _container = new(() =>
    {
        var o = options.Value;
        var client = new BlobContainerClient(o.AzureBlobConnectionString, o.AzureBlobContainer);
        client.CreateIfNotExists();
        return client;
    });

    public async Task WriteAsync(Guid tenantId, Guid fileId, Stream content, CancellationToken cancellationToken = default) =>
        await _container.Value.GetBlobClient($"{tenantId:N}/{fileId:N}").UploadAsync(content, overwrite: true, cancellationToken);

    public async Task<Stream?> OpenReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var blob = _container.Value.GetBlobClient($"{tenantId:N}/{fileId:N}");
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        return await blob.OpenReadAsync(cancellationToken: cancellationToken);
    }
}
