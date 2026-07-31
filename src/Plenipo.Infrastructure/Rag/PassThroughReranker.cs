using Plenipo.Application.Rag;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// No reranking: fusion order, truncated. Registered when <c>Rag:Reranker=None</c>, and the exact
/// behaviour retrieval had before reranking existed — including asking for no extra candidates, so
/// turning reranking off costs nothing rather than merely discarding the extra work.
/// </summary>
public sealed class PassThroughReranker : IRagReranker
{
    public string Name => "none";

    public bool UsesEmbeddings => false;

    public int CandidateCountFor(int topK) => topK;

    public Task<IReadOnlyList<RagHit>> RerankAsync(
        string query, IReadOnlyList<RagCandidate> candidates, int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return Task.FromResult<IReadOnlyList<RagHit>>([.. candidates.Take(topK).Select(c => c.Hit)]);
    }
}
