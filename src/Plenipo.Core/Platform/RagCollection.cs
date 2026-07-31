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

    /// <summary>
    /// Default Postgres text-search configuration for this collection's documents (e.g. "english",
    /// "spanish", "simple"). Stemming and stop-words are language-specific, so a corpus of Spanish
    /// contracts indexed as English retrieves badly — this is what makes the lexical arm work outside
    /// English. Per-document detection can override it; see <see cref="RagChunk.Language"/>.
    /// </summary>
    public string Language { get; set; } = RagLanguage.Default;

    /// <summary>
    /// Every text-search configuration actually present in this collection's chunks, maintained at
    /// ingest. Retrieval needs the set upfront: the lexical arm builds one constant
    /// <c>plainto_tsquery</c> per configuration so the GIN index stays usable, which a per-row
    /// <c>regconfig</c> would defeat.
    /// </summary>
    public List<string> IndexedLanguages { get; set; } = [];

    /// <summary>
    /// Free-form facets describing the whole corpus (e.g. jurisdiction=ES, lawArea=employment).
    /// Domain-defined — the platform never interprets the keys, it only filters on them, which is
    /// what lets one design serve legal, property, finance, and anything else.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// One retrievable passage of an ingested document. The embedding and the full-text search vector
/// live in SQL-only columns (pgvector <c>embedding</c>, <c>tsv</c>) created by the migration and
/// queried via raw SQL — they are deliberately unmapped so non-Postgres test providers never see
/// them. Every chunk carries its provenance (file id + name + ordinal), which is what makes cited
/// answers possible.
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

    /// <summary>
    /// First and last page this passage covers, when the source was paginated and the extractor
    /// could report page boundaries (a PDF text layer, or OCR from an engine that reports spans).
    /// Null for plain text and for extractors that cannot tell — a citation then names the file
    /// alone rather than inventing a page. A passage straddling a break has <c>PageFrom &lt; PageTo</c>.
    /// </summary>
    public int? PageFrom { get; set; }

    public int? PageTo { get; set; }

    /// <summary>
    /// The text-search configuration this chunk's <c>tsv</c> was built with — per chunk, because one
    /// case can hold documents in several languages and each needs its own stemmer.
    /// </summary>
    public string Language { get; set; } = RagLanguage.Default;

    /// <summary>
    /// Who may retrieve this passage, as opaque principal strings (<c>user:{id}</c>, <c>role:{name}</c>,
    /// <c>group:{externalId}</c>). EMPTY MEANS "no extra restriction" — the collection's gate already
    /// decided who is in scope, and this list narrows further within it (a partner-only memo inside a
    /// shared matter). Non-empty is enforced as set overlap against the caller's principals, inside
    /// both retrieval arms.
    /// </summary>
    public List<string> Principals { get; set; } = [];

    /// <summary>
    /// Per-passage facets, inherited from the document and overridable per source (e.g.
    /// jurisdiction=DE, effectiveYear=2024). Filtered with jsonb containment inside both arms.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// The Postgres text-search configurations the platform will index and query with. Kept to the set
/// Postgres ships by default so any deployment works without extra dictionaries; anything unknown
/// falls back to <see cref="Default"/>, which stems nothing and stops nothing — poor recall, but
/// never wrong, which is the right failure mode for a language we cannot analyse.
/// </summary>
public static class RagLanguage
{
    /// <summary>Language-neutral configuration: no stemming, no stop-words.</summary>
    public const string Default = "simple";

    /// <summary>Configurations bundled with a stock Postgres install.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "simple", "arabic", "armenian", "basque", "catalan", "danish", "dutch", "english", "finnish",
        "french", "german", "greek", "hindi", "hungarian", "indonesian", "irish", "italian",
        "lithuanian", "nepali", "norwegian", "portuguese", "romanian", "russian", "serbian",
        "spanish", "swedish", "tamil", "turkish", "yiddish",
    };

    /// <summary>
    /// The configuration to actually use — the requested one when Postgres knows it, otherwise
    /// <see cref="Default"/>. Also the injection guard: only values from <see cref="Supported"/> ever
    /// reach a <c>regconfig</c> cast.
    /// </summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return Default;
        }

        var trimmed = language.Trim().ToLowerInvariant();
        return Supported.Contains(trimmed) ? trimmed : Default;
    }
}
