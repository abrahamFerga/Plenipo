using Plenipo.Core.Platform;

namespace Plenipo.Application.Files;

/// <summary>
/// The platform file store: tenant-scoped metadata (a <see cref="StoredFile"/> row) plus content in
/// the configured blob backend. Everything the chat surfaces and the agent's document tools do with
/// files goes through this seam, so swapping local-disk for Azure Blob Storage is configuration.
/// </summary>
public interface IFileStore
{
    /// <summary>Persists content + metadata for the current tenant/user and returns the metadata row.</summary>
    public Task<StoredFile> SaveAsync(
        string fileName, string contentType, Stream content, string source, CancellationToken cancellationToken = default);

    /// <summary>The metadata row, tenant-scoped. Null when the id doesn't exist in this tenant.</summary>
    public Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>Opens the content for reading, tenant-scoped. Null when the id doesn't exist in this tenant.</summary>
    public Task<Stream?> OpenReadAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>The current user's most recent files (newest first).</summary>
    public Task<IReadOnlyList<StoredFile>> ListMineAsync(int take = 20, CancellationToken cancellationToken = default);
}

/// <summary>
/// Content backend behind <see cref="IFileStore"/>. Implementations: local disk (dev default) and
/// Azure Blob Storage. Keys are "{tenantId}/{fileId}" so tenant isolation holds at the storage layer too.
/// </summary>
public interface IFileBlobStorage
{
    public Task WriteAsync(Guid tenantId, Guid fileId, Stream content, CancellationToken cancellationToken = default);

    public Task<Stream?> OpenReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken = default);
}
