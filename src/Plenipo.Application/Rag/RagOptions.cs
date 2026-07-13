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
}
