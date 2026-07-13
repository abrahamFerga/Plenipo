namespace Plenipo.Application.Rag;

/// <summary>
/// The platform's ingestion job (module code enqueues it via <c>IJobQueue</c>; the platform's
/// handler executes it under the enqueuer's captured authority).
/// </summary>
public static class RagIngestJob
{
    public const string Kind = "platform.rag-ingest";
}

/// <summary>Arguments for a <see cref="RagIngestJob"/> job.</summary>
public sealed record RagIngestArgs(Guid CollectionId, IReadOnlyList<Guid> FileIds);

/// <summary>One retrieved passage with its provenance — everything a cited answer needs.</summary>
public sealed record RagHit(
    Guid ChunkId,
    Guid CollectionId,
    string CollectionName,
    Guid FileId,
    string FileName,
    int Ordinal,
    string Text,
    double Score);

/// <summary>
/// The platform's permission-aware retrieval service (see docs/PLATFORM_CONNECTORS_RAG_PLAN.md,
/// Part 3). Corpora are scoped <em>collections</em>; search is hybrid (full-text + vector, fused
/// with reciprocal rank fusion) with tenant and collection predicates inside both arms; access to a
/// resource-bound collection is gated through the owning module's <see cref="IRagCollectionGate"/>
/// and fails closed. Registered only when <c>Rag:Enabled</c> is true.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Finds or creates the collection for a module resource (e.g. legal matter) or, when
    /// <paramref name="resourceType"/> is null, a named module-level knowledge base.
    /// </summary>
    public Task<Guid> GetOrCreateCollectionAsync(
        string moduleId, string? resourceType, Guid? resourceId, string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts, chunks, embeds, and stores one file into a collection. Idempotent: existing chunks
    /// for the file are replaced. Returns the number of chunks stored (0 when unreadable).
    /// </summary>
    public Task<int> IngestFileAsync(Guid collectionId, Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search across every collection the caller may access (optionally narrowed to one by
    /// name). Collections whose gate denies — or whose resource type has no registered gate — are
    /// excluded before the query runs, and the final hits are re-checked (fail closed).
    /// </summary>
    public Task<IReadOnlyList<RagHit>> SearchAsync(
        string query, string? collectionName = null, int? topK = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A module's access gate for its resource-bound collections — the coarse layer of the two-layer
/// RAG authorization model (the wall/scope check, ahead of chunk-level trimming). Modules register
/// one per resource type; a bound collection with no matching gate is unqueryable by design.
/// </summary>
public interface IRagCollectionGate
{
    /// <summary>The <c>RagCollection.ResourceType</c> this gate covers (e.g. "matter").</summary>
    public string ResourceType { get; }

    /// <summary>Whether the current caller may query collections bound to this resource.</summary>
    public Task<bool> CanQueryAsync(Guid resourceId, CancellationToken cancellationToken = default);
}
