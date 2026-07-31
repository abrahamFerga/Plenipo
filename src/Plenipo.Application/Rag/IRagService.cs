namespace Plenipo.Application.Rag;

/// <summary>
/// The platform's ingestion job (module code enqueues it via <c>IJobQueue</c>; the platform's
/// handler executes it under the enqueuer's captured authority).
/// </summary>
public static class RagIngestJob
{
    public const string Kind = "platform.rag-ingest";
}

/// <summary>
/// Arguments for a <see cref="RagIngestJob"/> job. The optional members travel with the files so a
/// connector sync or a module tool can stamp provenance-derived access and facets at ingest time
/// without a second pass.
/// </summary>
public sealed record RagIngestArgs(
    Guid CollectionId,
    IReadOnlyList<Guid> FileIds,
    IReadOnlyList<string>? Principals = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? Language = null);

/// <summary>How one document should be indexed. Every member is optional — the collection supplies defaults.</summary>
public sealed record RagIngestOptions
{
    /// <summary>
    /// Who may retrieve the resulting passages, beyond the collection gate. Empty = no extra
    /// restriction. Build the strings with <see cref="RagPrincipals"/>.
    /// </summary>
    public IReadOnlyList<string>? Principals { get; init; }

    /// <summary>Facets stamped on every chunk of this document (jurisdiction, effective year, …).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Text-search configuration override. Null = detect from the document's own text, falling back
    /// to the collection's language.
    /// </summary>
    public string? Language { get; init; }
}

/// <summary>One retrieved passage with its provenance — everything a cited answer needs.</summary>
/// <param name="PageFrom">
/// First page of the passage, or null when the source was not paginated (plain text) or the
/// extractor could not report page boundaries. Never guessed.
/// </param>
/// <param name="PageTo">Last page — equal to <paramref name="PageFrom"/> unless the passage straddles a break.</param>
public sealed record RagHit(
    Guid ChunkId,
    Guid CollectionId,
    string CollectionName,
    Guid FileId,
    string FileName,
    int Ordinal,
    string Text,
    double Score,
    int? PageFrom = null,
    int? PageTo = null)
{
    /// <summary>"p. 4", "pp. 3–4", or null when this passage has no page information to cite.</summary>
    public string? PageCitation
    {
        get
        {
            if (PageFrom is not int from)
            {
                return null;
            }

            var to = PageTo ?? from;
            return to > from ? $"pp. {from}–{to}" : $"p. {from}";
        }
    }
}

/// <summary>A collection as seen from outside: what it covers and what can be filtered on.</summary>
public sealed record RagCollectionInfo(
    Guid Id,
    string ModuleId,
    string? ResourceType,
    Guid? ResourceId,
    string Name,
    string Language,
    string EmbeddingModel,
    int DocumentCount,
    int ChunkCount,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<string> FilterKeys);

/// <summary>
/// Opaque principal strings used for chunk-level trimming. Opaque on purpose: the platform compares
/// them as a set and never parses them, so a connector can contribute a source's own group ids
/// (<c>group:S-1-5-…</c>) alongside platform users and roles without the retrieval path learning
/// anything about either identity system.
/// </summary>
public static class RagPrincipals
{
    public static string User(Guid userId) => $"user:{userId}";

    public static string Role(string role) => $"role:{role}";

    /// <summary>An external group/principal id as reported by a connector's source system.</summary>
    public static string Group(string externalId) => $"group:{externalId}";
}

/// <summary>
/// Resolves the principal set for the current caller — the right-hand side of chunk-level ACL
/// trimming. The default implementation contributes the platform user and their roles; a module or
/// connector can replace or supplement it to add source-system groups.
/// </summary>
public interface IRagPrincipalResolver
{
    public Task<IReadOnlyList<string>> GetPrincipalsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The platform's permission-aware retrieval service (see docs/PLATFORM_CONNECTORS_RAG_PLAN.md,
/// Part 3). Corpora are scoped <em>collections</em>; search is hybrid (full-text + vector, fused
/// with reciprocal rank fusion) with tenant, collection, ACL and metadata predicates inside both
/// arms; access to a resource-bound collection is gated through the owning module's
/// <see cref="IRagCollectionGate"/> and fails closed. Registered only when <c>Rag:Enabled</c> is true.
/// </summary>
public interface IRagService
{
    /// <summary>
    /// Finds or creates the collection for a module resource (e.g. legal matter) or, when
    /// <paramref name="resourceType"/> is null, a named module-level knowledge base.
    /// </summary>
    public Task<Guid> GetOrCreateCollectionAsync(
        string moduleId, string? resourceType, Guid? resourceId, string name,
        string? language = null, IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts, chunks, embeds, and stores one file into a collection. Idempotent: existing chunks
    /// for the file are replaced. Returns the number of chunks stored (0 when unreadable).
    /// </summary>
    public Task<int> IngestFileAsync(
        Guid collectionId, Guid fileId, RagIngestOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search across every collection the caller may access (optionally narrowed to one by
    /// name, to the active agent's collection scopes, and to metadata facets). Collections whose
    /// gate denies — or whose resource type has no registered gate — are excluded before the query
    /// runs, and the final hits are re-checked (fail closed).
    /// </summary>
    public Task<IReadOnlyList<RagHit>> SearchAsync(
        string query, string? collectionName = null, int? topK = null,
        IReadOnlyDictionary<string, string>? filters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The collections the caller may query right now, with their size and filterable keys. Backs
    /// both the agent's discovery tool and the admin surface — an agent that can enumerate its own
    /// corpora stops guessing collection names.
    /// </summary>
    public Task<IReadOnlyList<RagCollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default);
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
