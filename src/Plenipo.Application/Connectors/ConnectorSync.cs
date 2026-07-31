namespace Plenipo.Application.Connectors;

/// <summary>The platform's connector-sync job (modules enqueue it via <c>IJobQueue</c>).</summary>
public static class ConnectorSyncJob
{
    public const string Kind = "platform.connector-sync";
}

/// <summary>Arguments for a <see cref="ConnectorSyncJob"/> job.</summary>
public sealed record ConnectorSyncArgs(Guid BindingId);

/// <summary>
/// How module code creates and finds sync bindings (one per resource — creating a binding for an
/// already-bound resource replaces the previous external ref and resets its sync state).
/// </summary>
public interface IConnectorBindingService
{
    public Task<Guid> BindAsync(
        string connectorId, string moduleId, string resourceType, Guid resourceId, string externalRef,
        CancellationToken cancellationToken = default);

    /// <summary>The resource's binding id, or null when it has none.</summary>
    public Task<Guid?> FindAsync(
        string moduleId, string resourceType, Guid resourceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One file the platform just imported, with whatever access and facet information the source
/// reported alongside it. Handlers pass <see cref="Principals"/> and <see cref="Metadata"/> straight
/// through to ingestion so a synced document keeps its source's restrictions instead of becoming
/// readable by everyone who can see the resource.
/// </summary>
public sealed record SyncedFile(
    Guid FileId,
    IReadOnlyList<string>? Principals = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// The module side of a sync: after the platform imports a binding's new/changed files into the
/// tenant file store, the handler for the binding's resource type takes over — attach them to the
/// resource, index them into its knowledge collection, whatever the domain needs. No handler for a
/// resource type means the sync fails loudly rather than importing files nobody owns.
/// </summary>
public interface IConnectorSyncHandler
{
    /// <summary>The <c>ConnectorBinding.ResourceType</c> this handler covers (e.g. "matter").</summary>
    public string ResourceType { get; }

    /// <summary>Called with the newly imported (new or changed) files and their source-reported access.</summary>
    public Task OnFilesSyncedAsync(Guid resourceId, IReadOnlyList<SyncedFile> files, CancellationToken cancellationToken = default);
}
