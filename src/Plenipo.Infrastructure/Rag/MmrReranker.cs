using Plenipo.Application.Rag;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Maximal Marginal Relevance: pick the next passage that is most relevant to the query while being
/// least like what has already been picked.
/// <para>
/// This exists because of what a large corpus actually looks like. A case with thousands of
/// documents contains the same boilerplate clause dozens of times, and pure relevance ranking fills
/// the whole answer window with near-identical copies of it — the model then sees one fact repeated
/// eight times instead of eight facts. MMR spends part of the window on the second-best *different*
/// thing, which is what makes an answer complete rather than merely confident.
/// </para>
/// <para>
/// It is the keyless default because it needs nothing the pipeline does not already have: the
/// candidate vectors are read alongside the passages, the arithmetic is a few thousand
/// multiplications over a few dozen candidates, and the result is deterministic — the same query
/// against the same corpus always ranks the same way, which matters when an answer has to be
/// defensible.
/// </para>
/// </summary>
public sealed class MmrReranker(IOptions<RagOptions> ragOptions) : IRagReranker
{
    public string Name => "mmr";

    /// <summary>Diversity is measured between the candidates' own vectors, so they are required.</summary>
    public bool UsesEmbeddings => true;

    /// <summary>
    /// Reranking is only as good as the shortlist it sees: diversification cannot promote a passage
    /// retrieval never fetched. The multiplier buys a deeper pool without paying for a deep one when
    /// the caller only wants a couple of hits.
    /// </summary>
    public int CandidateCountFor(int topK) =>
        Math.Clamp(topK * Math.Max(1, ragOptions.Value.RerankCandidateMultiplier), topK, RagOptions.MaxRerankCandidates);

    public Task<IReadOnlyList<RagHit>> RerankAsync(
        string query, IReadOnlyList<RagCandidate> candidates, int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var lambda = Math.Clamp(ragOptions.Value.MmrLambda, 0d, 1d);
        var usable = candidates.Where(c => c.Embedding.Count > 0).ToList();

        // Without vectors there is no notion of "like what we already picked". Fusion order is the
        // honest answer, not a silently degraded one.
        if (usable.Count < 2 || topK <= 1 || lambda >= 1d)
        {
            return Task.FromResult<IReadOnlyList<RagHit>>([.. candidates.Take(topK).Select(c => c.Hit)]);
        }

        // Relevance comes from the fusion score, normalised to [0,1] so it is on the same scale as
        // the cosine similarity it gets traded against. RRF scores are tiny and rank-derived, so the
        // absolute values carry no meaning — only their order does, which normalising preserves.
        var relevance = Normalize([.. candidates.Select(c => c.FusionScore)]);
        var vectors = candidates.Select(c => Unit(c.Embedding)).ToList();

        var selected = new List<int>(Math.Min(topK, candidates.Count));
        var remaining = new List<int>(Enumerable.Range(0, candidates.Count));

        while (selected.Count < topK && remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bestIndex = 0;
            var bestScore = double.NegativeInfinity;

            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                var redundancy = 0d;
                foreach (var chosen in selected)
                {
                    redundancy = Math.Max(redundancy, Cosine(vectors[candidate], vectors[chosen]));
                }

                var score = (lambda * relevance[candidate]) - ((1 - lambda) * redundancy);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            selected.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return Task.FromResult<IReadOnlyList<RagHit>>([.. selected.Select(i => candidates[i].Hit)]);
    }

    /// <summary>Min-max to [0,1]; an all-equal set maps to 1 so relevance stops discriminating and diversity decides.</summary>
    private static double[] Normalize(double[] scores)
    {
        var min = scores.Min();
        var max = scores.Max();
        var range = max - min;

        if (range <= double.Epsilon)
        {
            return [.. scores.Select(_ => 1d)];
        }

        return [.. scores.Select(s => (s - min) / range)];
    }

    /// <summary>
    /// Unit-normalised copy, so the similarity below is a plain dot product. Embeddings from some
    /// providers already arrive normalised; doing it again is cheap and makes the maths independent
    /// of which one produced them.
    /// </summary>
    private static float[] Unit(IReadOnlyList<float> vector)
    {
        var copy = new float[vector.Count];
        double norm = 0;
        for (var i = 0; i < vector.Count; i++)
        {
            copy[i] = vector[i];
            norm += (double)vector[i] * vector[i];
        }

        norm = Math.Sqrt(norm);
        if (norm <= double.Epsilon)
        {
            return copy;
        }

        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = (float)(copy[i] / norm);
        }

        return copy;
    }

    private static double Cosine(float[] a, float[] b)
    {
        // Vectors from different embedding models can differ in length; compare what they share
        // rather than throwing, since retrieval already pins the model and this is belt-and-braces.
        var length = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (var i = 0; i < length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot;
    }
}
