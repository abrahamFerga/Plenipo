using System.Text.RegularExpressions;

namespace Plenipo.Infrastructure.Ai;

/// <summary>
/// Narrows OpenAI's account-wide model catalog to the models a chat assistant can actually talk to.
/// <para>
/// OpenAI's <c>/v1/models</c> returns every model the account can reach — image generation, TTS,
/// transcription, embeddings, moderation, legacy completions — in one list of well over a hundred
/// ids, and the response carries no capability field to filter on (only <c>id</c>, <c>object</c>,
/// <c>created</c>, <c>owned_by</c>). Name patterns are therefore the only signal available, which is
/// why this exists and why it will need occasional amendment as OpenAI names new families.
/// </para>
/// <para>
/// OpenAI-only, deliberately. Anthropic publishes nothing but chat models. An Ollama install
/// commonly has embedding models pulled, but its ids are arbitrary local names — the family gate
/// below is default-deny, so applying this to Ollama would return an empty catalog. Azure exposes
/// operator-chosen deployment names and no catalog at all.
/// </para>
/// </summary>
public static partial class OpenAiChatModelFilter
{
    /// <summary>
    /// Substrings that mark a NON-chat modality. Spelled out in full rather than relying on short
    /// fragments matching by accident: an earlier version of this list in a product carried
    /// <c>"search"</c> to catch <c>o3-deep-research</c> (via "re|search") and silently took
    /// <c>gpt-4o-search-preview</c> — a real Chat Completions model — with it.
    /// </summary>
    private static readonly string[] NonChatMarkers =
    [
        "image", "dall-e", "sora", "audio", "realtime", "tts", "transcribe", "whisper",
        "embed", "moderation", "deep-research", "computer-use", "instruct",
        "davinci", "babbage", "codex",
    ];

    /// <summary>
    /// True when the id names a model reachable over Chat Completions.
    /// <para>
    /// Dated snapshots (<c>gpt-4.1-2025-04-14</c>) are KEPT: pinning a snapshot is the standard way
    /// to get reproducible behaviour in production, so suppressing them would be a deployment policy
    /// dressed up as a capability fact — not this function's call to make.
    /// </para>
    /// </summary>
    public static bool IsChatCompletionModel(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var model = id.Trim().ToLowerInvariant();

        // Family gate, default-deny: everything OpenAI serves over Chat Completions is a gpt-*,
        // chatgpt-*, or reasoning o-series id. A new family outside these disappears until this
        // list learns it — the admin can still type an id by hand, which is the escape hatch that
        // makes default-deny acceptable here.
        if (!model.StartsWith("gpt-", StringComparison.Ordinal)
            && !model.StartsWith("chatgpt-", StringComparison.Ordinal)
            && !OSeries().IsMatch(model))
        {
            return false;
        }

        if (NonChatMarkers.Any(marker => model.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        // The -pro tiers (o1-pro, o3-pro, gpt-5-pro) are Responses-API only, not Chat Completions.
        return !model.EndsWith("-pro", StringComparison.Ordinal)
               && !model.Contains("-pro-", StringComparison.Ordinal);
    }

    /// <summary>The chat-capable subset, preserving the caller's ordering.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> ids) =>
        [.. (ids ?? throw new ArgumentNullException(nameof(ids))).Where(IsChatCompletionModel)];

    // Reasoning series: o1, o3, o4-mini… Anchored so "omni-moderation-latest" is not mistaken for one.
    [GeneratedRegex("^o[0-9]+(-|$)")]
    private static partial Regex OSeries();
}
