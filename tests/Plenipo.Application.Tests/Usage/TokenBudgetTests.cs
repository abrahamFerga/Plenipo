using Plenipo.Application.Usage;

namespace Plenipo.Application.Tests.Usage;

public sealed class TokenBudgetTests
{
    [Fact]
    public void ZeroBudget_IsUnlimited()
    {
        Assert.False(TokenBudget.IsExceeded(0, 0));
        Assert.False(TokenBudget.IsExceeded(1_000_000, 0));
    }

    [Fact]
    public void UnderBudget_IsAllowed()
    {
        Assert.False(TokenBudget.IsExceeded(99, 100));
    }

    [Theory]
    [InlineData(100, 100)] // reaching the cap blocks the next turn
    [InlineData(101, 100)] // over the cap blocks too
    public void AtOrOverBudget_IsExceeded(long consumed, int budget)
    {
        Assert.True(TokenBudget.IsExceeded(consumed, budget));
    }
}
