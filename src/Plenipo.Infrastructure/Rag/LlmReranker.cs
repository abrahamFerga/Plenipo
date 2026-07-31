using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Plenipo.Application.Ai;
using Plenipo.Application.Rag;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Scores each candidate passage against the query with the tenant's chat model — the
/// cross-encoder pattern every high-end retrieval stack uses, without adding a second model
/// deployment to operate.
/// <para>
/// Bi-encoder retrieval (what the vector arm does) embeds the query and the passage separately and
/// compares the results, so it can only ever measure "these are about similar things". A
/// cross-encoder reads the query and the passage together and can judge whether the passage
/// actually <em>answers</em> the question. That difference is most of the precision gap between a
/// demo and a product, which is why it is worth an extra model call.
/// </para>
/// <para>
/// Opt-in (<c>Rag:Reranker=Llm</c>), because it costs a call and latency on every search. It fails
/// soft in every direction: no provider, a refusal, malformed output, a missing score — any of them
/// yields the retrieval order rather than an error, since a slightly worse ordering beats a failed
/// search.
/// </para>
/// </summary>
public sealed partial class LlmReranker(
    ITenantAiSettings tenantAiSettings,
    ITenantChatClientResolver chatClients,
    IOptions<RagOptions> ragOptions,
    ILogger<LlmReranker> logger) : IRagReranker
{
    /// <summary>Passage text sent per candidate — enough to judge relevance, bounded so the prompt stays small.</summary>
    private const int MaxPassageChars = 700;

    public string Name => "llm";

    /// <summary>The model reads the passage text; vectors would be dead weight in the prompt.</summary>
    public bool UsesEmbeddings => false;

    public int CandidateCountFor(int topK) =>
        Math.Clamp(topK * Math.Max(1, ragOptions.Value.RerankCandidateMultiplier), topK, RagOptions.MaxRerankCandidates);

    public async Task<IReadOnlyList<RagHit>> RerankAsync(
        string query, IReadOnlyList<RagCandidate> candidates, int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        IReadOnlyList<RagHit> Fallback() => [.. candidates.Take(topK).Select(c => c.Hit)];

        if (candidates.Count <= 1 || topK >= candidates.Count)
        {
            return Fallback(); // nothing to re-order, so don't pay for a call
        }

        try
        {
            var settings = await tenantAiSettings.ResolveAsync(cancellationToken);
            var model = string.IsNullOrWhiteSpace(ragOptions.Value.RerankerModel) ? null : ragOptions.Value.RerankerModel;
            var client = await chatClients.ResolveAsync(settings, model, cancellationToken);
            if (client is null)
            {
                return Fallback(); // provider is "None" for this tenant
            }

            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System,
                        "You rank search results. For each numbered passage, judge how well it answers the user's " +
                        "question. Reply with one line per passage in the form `<number>: <score>` where score is an " +
                        "integer from 0 (irrelevant) to 10 (directly answers it). Output nothing else — no prose, no " +
                        "explanation. Score every passage you are given."),
                    new ChatMessage(ChatRole.User, BuildPrompt(query, candidates)),
                ],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 16 * candidates.Count + 64 },
                cancellationToken);

            var scores = ParseScores(response.Text, candidates.Count);
            if (scores.Count == 0)
            {
                logger.LogDebug("The rerank model returned no usable scores; keeping retrieval order.");
                return Fallback();
            }

            // Ties and unscored passages fall back to fusion order, so a partial answer from the
            // model still improves the ranking instead of scrambling it.
            return
            [
                .. candidates
                    .Select((candidate, index) => (candidate, index, score: scores.GetValueOrDefault(index, double.NegativeInfinity)))
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.index)
                    .Take(topK)
                    .Select(x => x.candidate.Hit),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A misconfigured connection, a rate limit, a content filter — none of them should turn
            // a search into an error the user sees.
            logger.LogWarning(ex, "LLM reranking failed; falling back to retrieval order.");
            return Fallback();
        }
    }

    private static string BuildPrompt(string query, IReadOnlyList<RagCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.Append("Question: ").AppendLine(query).AppendLine();
        for (var i = 0; i < candidates.Count; i++)
        {
            var text = candidates[i].Hit.Text;
            if (text.Length > MaxPassageChars)
            {
                text = text[..MaxPassageChars] + "…";
            }

            sb.Append('[').Append(i + 1).Append("] ").AppendLine(text.ReplaceLineEndings(" ")).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses "<c>3: 8</c>" lines into zero-based indices. Deliberately forgiving — models wrap the
    /// answer in fences, number with parentheses, or add a stray sentence — but strictly bounded:
    /// an index outside the candidate range is dropped rather than clamped onto the wrong passage.
    /// </summary>
    internal static Dictionary<int, double> ParseScores(string? text, int count)
    {
        var scores = new Dictionary<int, double>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return scores;
        }

        foreach (Match match in ScoreLine().Matches(text))
        {
            if (!int.TryParse(match.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                !double.TryParse(match.Groups[2].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
            {
                continue;
            }

            var index = number - 1; // the prompt numbers from 1
            if (index >= 0 && index < count)
            {
                scores.TryAdd(index, score);
            }
        }

        return scores;
    }

    /// <summary>A leading passage number, then a separator, then the score. Anchored per line.</summary>
    [GeneratedRegex(@"^\s*[\[\(]?(\d{1,3})[\]\)]?\s*[:.\-]\s*(-?\d+(?:\.\d+)?)", RegexOptions.Multiline)]
    private static partial Regex ScoreLine();
}
