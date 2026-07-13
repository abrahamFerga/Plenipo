using Plenipo.Application.Ai;

namespace Plenipo.Application.Tests.Ai;

/// <summary>
/// Covers <see cref="TenantAiSettingsValidator"/> — the admin-supplied AI-override guardrails. A negative
/// budget (which would silently disable the cap) and a system prompt longer than the storage column (which
/// would otherwise 500 at save time) are rejected; everything else — including nulls that clear the override
/// and the exact-length boundary — is accepted.
/// </summary>
public sealed class TenantAiSettingsValidatorTests
{
    [Fact]
    public void NullFields_AreValid_TheyClearTheOverride()
    {
        Assert.Null(TenantAiSettingsValidator.Validate(systemPrompt: null, maxConversationTokens: null));
    }

    [Theory]
    [InlineData(0)]        // unlimited
    [InlineData(1)]
    [InlineData(500_000)]
    public void NonNegativeBudget_IsValid(int budget)
    {
        Assert.Null(TenantAiSettingsValidator.Validate("a prompt", budget));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-500)]
    public void NegativeBudget_IsRejected(int budget)
    {
        var error = TenantAiSettingsValidator.Validate("a prompt", budget);

        Assert.NotNull(error);
        Assert.Contains("maxConversationTokens", error);
    }

    [Fact]
    public void SystemPrompt_AtTheMaxLength_IsValid()
    {
        var prompt = new string('a', TenantAiSettingsValidator.MaxSystemPromptLength);

        Assert.Null(TenantAiSettingsValidator.Validate(prompt, maxConversationTokens: null));
    }

    [Fact]
    public void SystemPrompt_OverTheMaxLength_IsRejected_BeforeItCanFailAtSaveTime()
    {
        var prompt = new string('a', TenantAiSettingsValidator.MaxSystemPromptLength + 1);

        var error = TenantAiSettingsValidator.Validate(prompt, maxConversationTokens: null);

        Assert.NotNull(error);
        Assert.Contains("systemPrompt", error);
    }
}
