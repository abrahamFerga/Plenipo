namespace Plenipo.Application.Ai;

/// <summary>
/// Applies an agent profile's tool selection (Foundry/Copilot-Studio-style agent building): the
/// profile lists tool-name patterns — exact names or a trailing-<c>*</c> prefix wildcard
/// (<c>ask_*</c>). The selection runs AFTER RBAC filtering and can only narrow it, so a profile
/// is a composition surface, never a privilege-escalation one.
/// </summary>
public static class AgentToolSelection
{
    /// <summary>True when the selection is absent/empty — meaning "every permitted tool".</summary>
    public static bool AllowsAll(IReadOnlyCollection<string>? patterns) =>
        patterns is null || patterns.Count == 0;

    public static bool Matches(IReadOnlyCollection<string>? patterns, string toolName)
    {
        if (AllowsAll(patterns))
        {
            return true;
        }

        foreach (var pattern in patterns!)
        {
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern[^1] == '*')
            {
                if (toolName.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
