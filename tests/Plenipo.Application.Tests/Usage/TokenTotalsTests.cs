using Plenipo.Application.Usage;

namespace Plenipo.Application.Tests.Usage;

/// <summary>
/// Covers <see cref="TokenTotals"/> — the per-turn reconciliation the agent runner applies to whatever a
/// provider reports. The billed total feeds both the audit log and the per-conversation budget check, so the
/// "never under-count" rule matters for correctness, not just tidiness.
/// </summary>
public sealed class TokenTotalsTests
{
    [Theory]
    [InlineData(0, 0, 0, false)]    // provider reported nothing (e.g. Ollama / streaming usage off)
    [InlineData(0, 0, 30, true)]    // total only
    [InlineData(10, 20, 0, true)]   // parts only, no total
    [InlineData(10, 20, 30, true)]  // everything
    public void Any_IsTrue_WhenAnyCountIsNonZero(long input, long output, long total, bool expected)
    {
        Assert.Equal(expected, TokenTotals.Any(input, output, total));
    }

    [Fact]
    public void Effective_PrefersTheReportedTotal_WhenPresent()
    {
        Assert.Equal(30, TokenTotals.Effective(inputTokens: 10, outputTokens: 20, totalTokens: 30));
    }

    [Fact]
    public void Effective_SumsTheParts_WhenNoTotalReported()
    {
        // A provider that reports only input/output (total 0) must still be billed the sum, never 0 — else
        // the conversation budget silently under-counts and never trips.
        Assert.Equal(30, TokenTotals.Effective(inputTokens: 10, outputTokens: 20, totalTokens: 0));
    }

    [Fact]
    public void Effective_TrustsTheReportedTotal_EvenWhenItDiffersFromTheParts()
    {
        // We bill what the provider says the total is; we do not second-guess it against the parts.
        Assert.Equal(100, TokenTotals.Effective(inputTokens: 10, outputTokens: 20, totalTokens: 100));
    }

    [Fact]
    public void Effective_IsZero_WhenNothingWasReported()
    {
        Assert.Equal(0, TokenTotals.Effective(0, 0, 0));
    }
}
