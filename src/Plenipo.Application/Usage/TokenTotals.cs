namespace Plenipo.Application.Usage;

/// <summary>
/// Reconciles the token counts a provider reports for a single turn. Providers are inconsistent: some report
/// a total, some report only input + output (leaving the total at 0), some report only a total (leaving the
/// parts at 0). The billed total prefers a reported total and otherwise sums the parts, so usage is never
/// under-counted — which matters because the recorded total feeds both the audit/billing log and the
/// per-conversation <see cref="TokenBudget"/> check.
/// </summary>
public static class TokenTotals
{
    /// <summary>True when the provider reported any usage at all for the turn.</summary>
    public static bool Any(long inputTokens, long outputTokens, long totalTokens) =>
        totalTokens != 0 || inputTokens != 0 || outputTokens != 0;

    /// <summary>
    /// The effective billed total: the provider's reported total when present, otherwise the sum of the parts.
    /// A reported total is trusted as-is (not second-guessed against the parts).
    /// </summary>
    public static long Effective(long inputTokens, long outputTokens, long totalTokens) =>
        totalTokens != 0 ? totalTokens : inputTokens + outputTokens;
}
