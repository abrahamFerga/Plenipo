using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// A scoped retrieval corpus — per matter, per project, or a tenant knowledge base (the Harvey-Vault
/// "many small databases" pattern, see docs/PLATFORM_CONNECTORS_RAG_PLAN.md). Retrieval is
/// scope-first: a query names the collections it may search, and each collection can be bound to a
/// module resource whose ACL gates access <em>before</em> any vector math happens.
/// </summary>
public sealed class RagCollection : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The module the collection belongs to (e.g. "legal").</summary>
    public required string ModuleId { get; set; }

    /// <summary>
    /// Optional binding to a module resource (e.g. "matter"). When set, the module's
    /// <c>IRagCollectionGate</c> for this type must allow the caller — no gate, no access (fail closed).
    /// </summary>
    public string? ResourceType { get; set; }

    public Guid? ResourceId { get; set; }

    /// <summary>Display/lookup name, unique enough per tenant for the agent to reference.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The embedding model this collection's chunks were built with. Vectors from different models
    /// are not comparable — a model change means re-embedding into a new stamp.
    /// </summary>
    public required string EmbeddingModel { get; set; }
}

/// <summary>
/// One retrievable passage of an ingested document. The embedding and the full-text search vector
/// live in SQL-only columns (pgvector <c>embedding</c>, generated <c>tsv</c>) created by the
/// migration and queried via raw SQL — they are deliberately unmapped so non-Postgres test
/// providers never see them. Every chunk carries its provenance (file id + name + ordinal), which
/// is what makes cited answers possible.
/// </summary>
public sealed class RagChunk : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid CollectionId { get; set; }

    /// <summary>The platform <c>StoredFile</c> this chunk came from — the citation target.</summary>
    public Guid FileId { get; set; }

    /// <summary>File-name snapshot at ingest time, for citations without a join.</summary>
    public required string FileName { get; set; }

    /// <summary>Position of this chunk within its document (0-based).</summary>
    public int Ordinal { get; set; }

    public required string Text { get; set; }

    /// <summary>The model that produced this chunk's embedding (stamped per row for migrations).</summary>
    public required string EmbeddingModel { get; set; }

    /// <summary>SHA-256 of <see cref="Text"/> — cheap change detection for re-ingest.</summary>
    public required string ContentHash { get; set; }
}
