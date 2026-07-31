namespace Plenipo.Application.Rag;

/// <summary>
/// A candidate passage on its way through reranking: the hit itself plus the fusion score and the
/// embedding it was retrieved with, so a reranker can reason about the passage without going back
/// to the database.
/// </summary>
/// <param name="Embedding">
/// The chunk's vector, or empty when the deployment could not supply one (a chunk embedded under a
/// different model, say). A reranker that needs vectors must degrade rather than fail.
/// </param>
public sealed record RagCandidate(RagHit Hit, double FusionScore, IReadOnlyList<float> Embedding);

/// <summary>
/// Re-orders the candidates hybrid retrieval found, and cuts them to the requested depth.
/// <para>
/// Retrieval and ranking are deliberately separate concerns. Hybrid search is cheap and recall-
/// oriented: it casts a wide net and fuses two arms that disagree about what "similar" means.
/// Reranking is the precision pass over that shortlist — it can afford to be slower per candidate
/// because there are only a few dozen of them.
/// </para>
/// <para>
/// A reranker may only re-order and truncate. It never widens the set, so every access decision
/// (agent scope, collection gate, chunk ACL, metadata filter) has already been made and cannot be
/// undone here. It must also never throw: a reranker that fails degrades to the retrieval order,
/// because a slightly worse ordering is always better than a failed search.
/// </para>
/// </summary>
public interface IRagReranker
{
    /// <summary>Short name for diagnostics and the admin surface (e.g. "mmr", "llm", "none").</summary>
    public string Name { get; }

    /// <summary>
    /// Whether <see cref="RagCandidate.Embedding"/> needs to be populated. Retrieval skips reading
    /// the vectors when nothing will look at them — for a shortlist of large embeddings that is a
    /// meaningful amount of data not to move.
    /// </summary>
    public bool UsesEmbeddings { get; }

    /// <summary>
    /// How many candidates this reranker wants for a given target depth. Retrieval fetches that
    /// many before handing them over — a reranker is only as good as the shortlist it sees, and a
    /// pass-through one should not make the query do extra work.
    /// </summary>
    public int CandidateCountFor(int topK);

    /// <summary>
    /// The final ordering, at most <paramref name="topK"/> long. Candidates arrive in fusion order.
    /// </summary>
    public Task<IReadOnlyList<RagHit>> RerankAsync(
        string query,
        IReadOnlyList<RagCandidate> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}
