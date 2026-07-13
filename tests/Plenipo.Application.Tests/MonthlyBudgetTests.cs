using Plenipo.Application.Ai;
using Plenipo.Application.Usage;

namespace Plenipo.Application.Tests;

public sealed class MonthlyBudgetTests
{
    [Fact]
    public void Merge_MonthlyBudget_NullInherits_ZeroMeansUnlimited()
    {
        var defaults = new AiOptions { MaxMonthlyTokens = 500_000 };

        Assert.Equal(500_000, EffectiveAiSettings.Merge(null, null, null, defaults).MaxMonthlyTokens);
        Assert.Equal(0, EffectiveAiSettings.Merge(null, null, 0, defaults).MaxMonthlyTokens);
        Assert.Equal(1_000_000, EffectiveAiSettings.Merge(null, null, 1_000_000, defaults).MaxMonthlyTokens);
    }

    [Theory]
    [InlineData(79, 81, true)]   // moved over the 80-token threshold
    [InlineData(80, 95, false)]  // already at/over it before the turn — alert already fired
    [InlineData(10, 79, false)]  // still below
    [InlineData(79, 80, true)]   // landing exactly on the threshold counts
    public void CrossedFraction_FiresOnlyOnTheCrossingTurn(long before, long after, bool expected)
    {
        Assert.Equal(expected, TokenBudget.CrossedFraction(before, after, budget: 100, fraction: 0.8));
    }

    [Fact]
    public void CrossedFraction_UnlimitedBudget_NeverFires()
    {
        Assert.False(TokenBudget.CrossedFraction(0, long.MaxValue, budget: 0, fraction: 0.8));
    }

    [Fact]
    public void Validator_RejectsNegativeMonthlyBudget()
    {
        Assert.NotNull(TenantAiSettingsValidator.Validate(null, null, -1));
        Assert.Null(TenantAiSettingsValidator.Validate(null, null, 0));
    }
}
