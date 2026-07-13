using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// A scoped sync binding: ONE external location (folder, container prefix, drive) bound to ONE
/// module resource (e.g. a legal matter) — the Harvey-Vault pattern, deliberately not global
/// indexing. The platform sync job walks the binding, imports new/changed files into the tenant
/// file store, and hands them to the owning module (which attaches and indexes them).
/// </summary>
public sealed class ConnectorBinding : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The connector that provides the files (manifest id, e.g. "local-folder").</summary>
    public required string ConnectorId { get; set; }

    /// <summary>The module owning the bound resource (e.g. "legal").</summary>
    public required string ModuleId { get; set; }

    /// <summary>The bound resource (e.g. "matter" + the matter id).</summary>
    public required string ResourceType { get; set; }

    public Guid ResourceId { get; set; }

    /// <summary>The external location within the connector (folder path, prefix, drive id).</summary>
    public required string ExternalRef { get; set; }

    /// <summary>
    /// Sync state: JSON map of external item id → { fileId, stamp }. An unchanged stamp skips the
    /// item, so re-syncs are incremental and never duplicate imports.
    /// </summary>
    public string? SyncedItemsJson { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
}
