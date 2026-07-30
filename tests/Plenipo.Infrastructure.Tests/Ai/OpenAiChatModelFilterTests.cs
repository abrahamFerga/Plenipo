using Plenipo.Infrastructure.Ai;

namespace Plenipo.Infrastructure.Tests.Ai;

/// <summary>
/// The chat-capability table. OpenAI's /v1/models carries no capability field, so these name
/// patterns are the only available signal — which makes the table itself the specification, and
/// makes each entry worth stating rather than inferring.
/// </summary>
public sealed class OpenAiChatModelFilterTests
{
    [Theory]
    // Chat families — kept.
    [InlineData("gpt-4o", true)]
    [InlineData("gpt-4o-mini", true)]
    [InlineData("gpt-4.1", true)]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-mini", true)]
    [InlineData("gpt-5-chat-latest", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("gpt-oss-120b", true)]
    // Reasoning o-series — kept, including two-digit families.
    [InlineData("o1", true)]
    [InlineData("o1-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    [InlineData("o10-mini", true)]
    // Other modalities — dropped.
    [InlineData("dall-e-3", false)]
    [InlineData("gpt-image-1", false)]
    [InlineData("sora-2", false)]
    [InlineData("tts-1-hd", false)]
    [InlineData("gpt-4o-audio-preview", false)]
    [InlineData("gpt-4o-realtime-preview", false)]
    [InlineData("gpt-4o-transcribe", false)]
    [InlineData("whisper-1", false)]
    [InlineData("text-embedding-3-large", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("codex-mini-latest", false)]
    [InlineData("computer-use-preview", false)]
    // Legacy completions — dropped.
    [InlineData("gpt-3.5-turbo-instruct", false)]
    [InlineData("davinci-002", false)]
    [InlineData("babbage-002", false)]
    // Responses-API-only -pro tiers — dropped.
    [InlineData("o1-pro", false)]
    [InlineData("o3-pro", false)]
    [InlineData("gpt-5-pro", false)]
    // Not a chat family at all.
    [InlineData("some-unknown-model", false)]
    public void The_capability_table(string id, bool expected) =>
        Assert.Equal(expected, OpenAiChatModelFilter.IsChatCompletionModel(id));

    [Fact]
    public void A_dated_snapshot_is_kept_because_pinning_one_is_a_deployment_choice()
    {
        // Suppressing pinned snapshots would be a reproducibility POLICY masquerading as a
        // capability filter — and this filter has no business making that call. Pinning
        // gpt-4.1-2025-04-14 is the standard way to hold behaviour still in production.
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("gpt-4.1-2025-04-14"));
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("gpt-4-0613"));
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("gpt-4-1106-preview"));
    }

    [Fact]
    public void A_search_preview_chat_model_survives_but_deep_research_does_not()
    {
        // These two are why the non-chat markers are spelled out in full. A short "search" marker
        // catches o3-deep-research through the substring in "re|search" — and takes
        // gpt-4o-search-preview, a real Chat Completions model, down with it by accident.
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("gpt-4o-search-preview"));
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("gpt-4o-mini-search-preview"));
        Assert.False(OpenAiChatModelFilter.IsChatCompletionModel("o3-deep-research"));
    }

    [Fact]
    public void Ids_are_matched_case_insensitively_and_trimmed()
    {
        Assert.True(OpenAiChatModelFilter.IsChatCompletionModel("  GPT-4o  "));
        Assert.False(OpenAiChatModelFilter.IsChatCompletionModel("DALL-E-3"));
    }

    [Fact]
    public void Filter_preserves_the_callers_ordering()
    {
        var kept = OpenAiChatModelFilter.Filter(["gpt-4o", "dall-e-3", "o3-mini", "whisper-1", "gpt-4o-mini"]);

        Assert.Equal(["gpt-4o", "o3-mini", "gpt-4o-mini"], kept);
    }
}
