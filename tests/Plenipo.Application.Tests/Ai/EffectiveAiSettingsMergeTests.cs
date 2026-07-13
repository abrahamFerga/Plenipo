using Plenipo.Application.Ai;

namespace Plenipo.Application.Tests.Ai;

/// <summary>
/// Covers <see cref="EffectiveAiSettings.Merge"/> — the per-tenant override layering that
/// <c>TenantAiSettingsResolver</c> applies over the deployment defaults. The null-handling is subtle: a
/// missing/blanked override inherits the default, but a token budget of 0 is a real value (unlimited),
/// distinct from null.
/// </summary>
public sealed class EffectiveAiSettingsMergeTests
{
    private static AiOptions Defaults() => new()
    {
        SystemPrompt = "deployment default prompt",
        MaxConversationTokens = 50_000,
    };

    [Fact]
    public void NoOverrides_InheritsBothDefaults()
    {
        var effective = EffectiveAiSettings.Merge(overrideSystemPrompt: null, overrideMaxConversationTokens: null, overrideMaxMonthlyTokens: null, Defaults());

        Assert.Equal("deployment default prompt", effective.SystemPrompt);
        Assert.Equal(50_000, effective.MaxConversationTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankOrMissingOverridePrompt_InheritsTheDefault(string? overridePrompt)
    {
        var effective = EffectiveAiSettings.Merge(overridePrompt, overrideMaxConversationTokens: null, overrideMaxMonthlyTokens: null, Defaults());

        Assert.Equal("deployment default prompt", effective.SystemPrompt);
    }

    [Fact]
    public void ProvidedOverrides_TakeEffect()
    {
        var effective = EffectiveAiSettings.Merge("tenant prompt", 5_000, overrideMaxMonthlyTokens: null, Defaults());

        Assert.Equal("tenant prompt", effective.SystemPrompt);
        Assert.Equal(5_000, effective.MaxConversationTokens);
    }

    [Fact]
    public void TenantTokenBudgetOfZero_IsHonouredAsUnlimited_NotTreatedAsUnset()
    {
        // 0 means "unlimited" (matching AiOptions) — a real override, distinct from null which inherits the default.
        var effective = EffectiveAiSettings.Merge(overrideSystemPrompt: null, overrideMaxConversationTokens: 0, overrideMaxMonthlyTokens: null, Defaults());

        Assert.Equal(0, effective.MaxConversationTokens);
    }

    [Fact]
    public void NullDefaults_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EffectiveAiSettings.Merge("prompt", 1, null, defaults: null!));
    }
}
