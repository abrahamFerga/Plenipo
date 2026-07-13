using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// Metadata for a file in the platform file store (chat attachments, agent-generated documents).
/// The bytes live in the configured blob backend (local disk in dev, Azure Blob Storage in prod);
/// this row is the tenant-scoped source of truth the API and agent tools resolve ids against.
/// </summary>
public sealed class StoredFile : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The user who uploaded (or whose agent turn generated) the file.</summary>
    public Guid UserId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the content (hex) — integrity checks and dedup diagnostics.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Where the file came from: "upload" or the name of the tool that generated it.</summary>
    public required string Source { get; set; }
}
