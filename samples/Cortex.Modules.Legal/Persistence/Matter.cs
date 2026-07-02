using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

public enum MatterStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// A legal matter — the engagement-scoped workspace every legal-AI product organizes around
/// (Harvey's Vault, Legora's workspaces). Documents, drafts, and agent work product attach to a
/// matter; tenant isolation applies via the module's query filters.
/// </summary>
public sealed class Matter : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>The client this matter is for (display-level; the PM system stays the system of record).</summary>
    public string? ClientName { get; set; }

    public MatterStatus Status { get; set; } = MatterStatus.Open;

    public ICollection<MatterDocument> Documents { get; set; } = [];
}

/// <summary>
/// A platform file (<c>StoredFile</c>) attached to a matter. The platform file store keeps the bytes
/// and metadata; this row is the matter-scoped association plus a display-name snapshot.
/// </summary>
public sealed class MatterDocument : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>The platform <c>StoredFile</c> id — what the document tools (read_document) consume.</summary>
    public Guid FileId { get; set; }

    public required string FileName { get; set; }

    /// <summary>Optional note recorded at attach time (e.g. "signed original", "client draft").</summary>
    public string? Note { get; set; }
}
