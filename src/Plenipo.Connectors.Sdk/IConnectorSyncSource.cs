namespace Plenipo.Connectors.Sdk;

/// <summary>One file visible under a sync source's external reference.</summary>
/// <param name="Id">Stable id within the source (path, blob name, drive-item id).</param>
/// <param name="Name">Display/file name.</param>
/// <param name="ContentType">MIME type (best effort).</param>
/// <param name="ContentStamp">
/// Change detector — hash, ETag, or last-modified ticks. Sync re-imports an item only when its
/// stamp changed, so re-running a sync is cheap and never duplicates.
/// </param>
public sealed record ConnectorSyncFile(string Id, string Name, string ContentType, string ContentStamp);

/// <summary>
/// The sync lane (Lane B) of a connector: enumerate and open files under an admin/tool-chosen
/// external reference (a folder path, a container prefix, a drive id). The platform's sync job
/// walks a resource-scoped binding (e.g. matter ↔ folder — one binding per resource, the
/// Harvey-Vault pattern), imports new/changed files into the tenant file store, and hands them to
/// the owning module. Implement alongside <see cref="IConnectorToolSource"/> and declare
/// <c>SupportsSync = true</c> in the manifest.
/// </summary>
public interface IConnectorSyncSource
{
    /// <summary>The connector this sync source belongs to. Must match the manifest id.</summary>
    public string ConnectorId { get; }

    /// <summary>
    /// The files currently under <paramref name="externalRef"/>, or null when the connector is not
    /// enabled/configured for the current tenant (fail closed, like the tools).
    /// </summary>
    public Task<IReadOnlyList<ConnectorSyncFile>?> ListAsync(string externalRef, CancellationToken cancellationToken = default);

    /// <summary>Opens one listed file's content, or null when it vanished since listing.</summary>
    public Task<Stream?> OpenAsync(string externalRef, string fileId, CancellationToken cancellationToken = default);
}
