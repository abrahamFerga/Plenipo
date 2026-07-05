namespace Cortex.Application.Usage;

/// <summary>
/// The per-conversation token-budget rule (a MAF production guardrail: "token budget enforced per
/// session"). A budget of 0 means unlimited. The check is deny-by-default once the budget is reached,
/// so an unbounded conversation cannot run up arbitrary cost.
/// </summary>
public static class TokenBudget
{
    /// <summary>True when a positive budget has been reached or exceeded by prior consumption.</summary>
    public static bool IsExceeded(long consumedTokens, long budget) =>
        budget > 0 && consumedTokens >= budget;

    /// <summary>
    /// True when this turn moved consumption from below to at-or-above <paramref name="fraction"/>
    /// of a positive budget — the once-per-crossing trigger for threshold alerts.
    /// </summary>
    public static bool CrossedFraction(long before, long after, long budget, double fraction)
    {
        if (budget <= 0)
        {
            return false;
        }

        var threshold = (long)(budget * fraction);
        return before < threshold && after >= threshold;
    }
}
