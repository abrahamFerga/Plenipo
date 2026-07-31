using Plenipo.Application.Ai;
using Plenipo.Application.Rag;
using Plenipo.Infrastructure.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The precision pass over the retrieved shortlist. The property that matters for MMR is that it
/// promotes the best DIFFERENT passage over a near-copy of one already chosen — without that, a
/// window of eight results can be eight versions of the same boilerplate clause.
/// </summary>
public sealed class RerankerTests
{
    private static MmrReranker Mmr(double lambda = 0.7, int multiplier = 5) =>
        new(Options.Create(new RagOptions { MmrLambda = lambda, RerankCandidateMultiplier = multiplier }));

    [Fact]
    public async Task Mmr_promotes_a_distinct_passage_over_a_near_duplicate()
    {
        // The shape a large corpus actually produces: the same boilerplate clause retrieved three
        // times at the top, with the passage that answers the question just behind it. Pure
        // relevance fills the window with copies; MMR spends the second slot on something new.
        var candidates = new List<RagCandidate>
        {
            Candidate("boilerplate A", 0.050, [1f, 0f, 0f]),
            Candidate("boilerplate B", 0.049, [0.99f, 0.01f, 0f]),
            Candidate("boilerplate C", 0.048, [0.98f, 0.02f, 0f]),
            Candidate("the actual answer", 0.045, [0f, 1f, 0f]),
            Candidate("unrelated", 0.020, [0f, 0f, 1f]),
        };

        var reranked = await Mmr().RerankAsync("q", candidates, topK: 2);

        Assert.Equal("boilerplate A", reranked[0].Text);   // the most relevant is still first
        Assert.Equal("the actual answer", reranked[1].Text); // ...but the second slot is not a copy
    }

    [Fact]
    public async Task Mmr_does_not_promote_diversity_over_a_large_relevance_gap()
    {
        // The other half of the contract, and the reason λ defaults to 0.7: a passage that is
        // merely DIFFERENT does not outrank one that is much more relevant. Diversity breaks near
        // ties; it does not overrule the ranking.
        var candidates = new List<RagCandidate>
        {
            Candidate("relevant A", 0.050, [1f, 0f, 0f]),
            Candidate("relevant B", 0.048, [0.99f, 0.01f, 0f]),
            Candidate("different but weak", 0.005, [0f, 1f, 0f]),
        };

        var reranked = await Mmr().RerankAsync("q", candidates, topK: 2);

        Assert.Equal(["relevant A", "relevant B"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Mmr_keeps_the_most_relevant_passage_first()
    {
        // Diversity must never cost the top hit — that would be a regression, not a feature.
        var candidates = new List<RagCandidate>
        {
            Candidate("best", 0.060, [1f, 0f, 0f]),
            Candidate("different", 0.010, [0f, 1f, 0f]),
            Candidate("also different", 0.009, [0f, 0f, 1f]),
        };

        var reranked = await Mmr().RerankAsync("q", candidates, topK: 3);

        Assert.Equal("best", reranked[0].Text);
        Assert.Equal(3, reranked.Count);
    }

    [Fact]
    public async Task Mmr_with_lambda_one_is_pure_relevance()
    {
        // λ=1 removes the diversity term entirely, which must reproduce fusion order exactly —
        // the escape hatch for anyone who wants reranking off without changing providers.
        var candidates = new List<RagCandidate>
        {
            Candidate("first", 0.050, [1f, 0f, 0f]),
            Candidate("near copy", 0.049, [0.99f, 0.01f, 0f]),
            Candidate("distinct", 0.048, [0f, 1f, 0f]),
        };

        var reranked = await Mmr(lambda: 1.0).RerankAsync("q", candidates, topK: 3);

        Assert.Equal(["first", "near copy", "distinct"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Mmr_falls_back_to_fusion_order_without_vectors()
    {
        // A chunk embedded under a different model contributes no vector. Degrade, never throw.
        var candidates = new List<RagCandidate>
        {
            Candidate("a", 0.050, []),
            Candidate("b", 0.040, []),
            Candidate("c", 0.030, []),
        };

        var reranked = await Mmr().RerankAsync("q", candidates, topK: 2);

        Assert.Equal(["a", "b"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Mmr_never_returns_more_than_asked_for()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => Candidate($"p{i}", 0.05 - (i * 0.001), [i, 1f, 0f]))
            .ToList();

        var reranked = await Mmr().RerankAsync("q", candidates, topK: 5);

        Assert.Equal(5, reranked.Count);
        Assert.Equal(5, reranked.Select(h => h.ChunkId).Distinct().Count()); // and no duplicates
    }

    [Fact]
    public void The_candidate_pool_is_deeper_than_the_answer_but_bounded()
    {
        // A reranker can only promote what retrieval fetched, so the pool has to be wider than the
        // result — and capped, or a topK of 50 would drag 250 candidates through the query.
        Assert.Equal(40, Mmr(multiplier: 5).CandidateCountFor(8));
        Assert.Equal(RagOptions.MaxRerankCandidates, Mmr(multiplier: 50).CandidateCountFor(8));
        Assert.Equal(8, new PassThroughReranker().CandidateCountFor(8)); // off costs nothing
    }

    [Fact]
    public async Task Pass_through_preserves_fusion_order_exactly()
    {
        var candidates = new List<RagCandidate>
        {
            Candidate("a", 0.050, [1f, 0f, 0f]),
            Candidate("b", 0.049, [0.99f, 0f, 0f]),
            Candidate("c", 0.048, [0.98f, 0f, 0f]),
        };

        var reranked = await new PassThroughReranker().RerankAsync("q", candidates, topK: 2);

        Assert.Equal(["a", "b"], reranked.Select(h => h.Text));
    }

    [Theory]
    // The shapes models actually emit: plain, bracketed, parenthesised, fenced, with prose.
    [InlineData("1: 9\n2: 3\n3: 7", 3, new[] { 9, 3, 7 })]
    [InlineData("[1] 8\n[2] 2", 0, new int[0])]              // no separator — not a score line
    [InlineData("1. 8\n2. 2", 2, new[] { 8, 2 })]
    [InlineData("(1): 5\n(2): 6", 2, new[] { 5, 6 })]
    [InlineData("```\n1: 4\n2: 6\n```", 2, new[] { 4, 6 })]
    [InlineData("Here are the scores:\n1: 10\n2: 0", 2, new[] { 10, 0 })]
    public void Llm_score_parsing_handles_the_shapes_models_emit(string text, int count, int[] expected)
    {
        var scores = LlmReranker.ParseScores(text, count == 0 ? 3 : count);

        Assert.Equal(expected.Length, scores.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], scores[i]);
        }
    }

    [Fact]
    public void Llm_score_parsing_drops_out_of_range_indices_rather_than_clamping()
    {
        // A model that numbers past the end must not have its score land on the wrong passage.
        var scores = LlmReranker.ParseScores("1: 9\n7: 10", count: 2);

        Assert.Single(scores);
        Assert.Equal(9, scores[0]);
    }

    [Fact]
    public void Llm_score_parsing_survives_junk() =>
        Assert.Empty(LlmReranker.ParseScores("I cannot help with that request.", count: 3));

    [Fact]
    public async Task Llm_reranker_reorders_by_the_scores_the_model_returns()
    {
        // The whole point of a cross-encoder: the passage the model judges most responsive wins,
        // even though fusion ranked it last.
        var reranker = LlmWith("1: 2\n2: 3\n3: 9");
        var candidates = new List<RagCandidate>
        {
            Candidate("fused first", 0.050, []),
            Candidate("fused second", 0.040, []),
            Candidate("actually answers it", 0.030, []),
        };

        var reranked = await reranker.RerankAsync("q", candidates, topK: 2);

        Assert.Equal(["actually answers it", "fused second"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Llm_reranker_keeps_retrieval_order_when_the_model_is_unusable()
    {
        // A refusal, a chatty answer, a content filter — none of them may fail the search. This is
        // exactly what the built-in Mock provider produces, so the degraded path is the common one
        // on a keyless deployment.
        var reranker = LlmWith("I'm sorry, I can't help with that.");
        var candidates = new List<RagCandidate>
        {
            Candidate("a", 0.050, []),
            Candidate("b", 0.040, []),
            Candidate("c", 0.030, []),
        };

        var reranked = await reranker.RerankAsync("q", candidates, topK: 2);

        Assert.Equal(["a", "b"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Llm_reranker_survives_a_provider_that_throws()
    {
        var reranker = LlmWith(_ => throw new InvalidOperationException("rate limited"));
        var candidates = new List<RagCandidate>
        {
            Candidate("a", 0.050, []),
            Candidate("b", 0.040, []),
        };

        var reranked = await reranker.RerankAsync("q", candidates, topK: 1);

        Assert.Equal(["a"], reranked.Select(h => h.Text));
    }

    [Fact]
    public async Task Llm_reranker_does_not_call_the_model_when_there_is_nothing_to_reorder()
    {
        // Asking for everything means the ordering cannot change the result set — paying for a
        // model call there would be pure waste.
        var calls = 0;
        var reranker = LlmWith(_ => { calls++; return "1: 9"; });

        await reranker.RerankAsync("q", [Candidate("only", 0.05, [])], topK: 5);

        Assert.Equal(0, calls);
    }

    private static LlmReranker LlmWith(string reply) => LlmWith(_ => reply);

    private static LlmReranker LlmWith(Func<string, string> reply) =>
        new(
            new StubAiSettings(),
            new StubChatClientResolver(new ScriptedChatClient(reply)),
            Options.Create(new RagOptions()),
            NullLogger<LlmReranker>.Instance);

    private static RagCandidate Candidate(string text, double score, float[] embedding) =>
        new(
            new RagHit(Guid.CreateVersion7(), Guid.Empty, "c", Guid.Empty, "f.txt", 0, text, score),
            score,
            embedding);

    private sealed class StubAiSettings : ITenantAiSettings
    {
        public Task<EffectiveAiSettings> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAiSettings("", 0, 0) { Provider = "Mock", Model = "mock" });
    }

    private sealed class StubChatClientResolver(IChatClient client) : ITenantChatClientResolver
    {
        public Task<IChatClient?> ResolveAsync(
            EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(client);
    }

    /// <summary>Answers with whatever the script says the model would say — including by throwing.</summary>
    private sealed class ScriptedChatClient(Func<string, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var prompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(prompt))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
