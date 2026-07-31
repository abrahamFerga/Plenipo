namespace Plenipo.Application.Rag;

/// <summary>
/// The opt-in RAG pipeline, bound from the "Rag" configuration section. Disabled by default — a
/// deployment that doesn't need retrieval pays nothing (no tools offered, no services registered).
/// Requires a Postgres with the pgvector extension available (the dev/CI images provide it).
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public bool Enabled { get; set; }

    /// <summary>
    /// One of: Mock, OpenAI, AzureOpenAI, Ollama. "Mock" is a deterministic, dependency-free
    /// bag-of-words embedder so ingestion and retrieval work — and are testable — with no API key,
    /// mirroring the chat Mock provider.
    /// </summary>
    public string EmbeddingProvider { get; set; } = "Mock";

    /// <summary>Embedding model id (e.g. "text-embedding-3-small"); ignored by Mock.</summary>
    public string EmbeddingModel { get; set; } = "mock-bow-384";

    /// <summary>Deployment credential for embeddings only; chat credentials are tenant-vaulted.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Endpoint for AzureOpenAI, or the Ollama OpenAI-compatible base URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Chunking target, in characters (~400 tokens by default).</summary>
    public int MaxChunkChars { get; set; } = 1800;

    /// <summary>Default number of passages a search returns.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>
    /// Default Postgres text-search configuration for new collections when none is given. "simple"
    /// (no stemming, no stop-words) is the safe default for a deployment serving many countries: it
    /// under-retrieves slightly rather than applying the wrong language's stemmer. A single-language
    /// deployment should set its own ("english", "spanish", …), and any collection can override.
    /// </summary>
    public string DefaultLanguage { get; set; } = "simple";

    /// <summary>
    /// Chunk count past which a collection gets its own approximate vector index instead of relying
    /// on exact scan. Below it, exact scan gives perfect recall for free; above it, latency grows
    /// linearly and an HNSW index is worth its build and maintenance cost.
    /// </summary>
    public int IndexThresholdChunks { get; set; } = 20_000;

    /// <summary>
    /// The precision pass over the retrieved shortlist. One of:
    /// <list type="bullet">
    /// <item><c>Mmr</c> (default) — maximal marginal relevance over the candidates' own vectors.
    /// Keyless, deterministic, and costs nothing but arithmetic; it stops a window full of the same
    /// boilerplate clause from crowding out the rest of the answer.</item>
    /// <item><c>Llm</c> — the tenant's chat model scores each passage against the query
    /// (cross-encoder). The most accurate option and the one every high-end stack uses, at the cost
    /// of a model call and its latency on every search.</item>
    /// <item><c>None</c> — fusion order, truncated. The behaviour before reranking existed.</item>
    /// </list>
    /// </summary>
    public string Reranker { get; set; } = "Mmr";

    /// <summary>
    /// How many candidates to retrieve per requested hit before reranking. A reranker can only
    /// re-order what retrieval fetched, so this is what gives it something to promote; the product
    /// is capped by <see cref="MaxRerankCandidates"/>. Ignored when <c>Reranker=None</c>.
    /// </summary>
    public int RerankCandidateMultiplier { get; set; } = 5;

    /// <summary>
    /// MMR's relevance/diversity trade-off: 1.0 is pure relevance (equivalent to no reranking), 0.0
    /// is pure diversity. The default leans hard toward relevance, so diversity only breaks ties
    /// between passages that are genuinely near-duplicates.
    /// </summary>
    public double MmrLambda { get; set; } = 0.7;

    /// <summary>Model for <c>Reranker=Llm</c>. Null/empty = the tenant's default chat model.</summary>
    public string? RerankerModel { get; set; }

    /// <summary>
    /// Ceiling on the candidate shortlist. Bounds the LLM reranker's prompt and MMR's O(n²)
    /// similarity work, both of which grow with the pool while the benefit flattens.
    /// </summary>
    public const int MaxRerankCandidates = 100;

}
