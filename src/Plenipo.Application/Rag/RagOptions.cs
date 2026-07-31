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

}
