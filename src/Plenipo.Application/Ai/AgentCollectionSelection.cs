namespace Plenipo.Application.Ai;

/// <summary>
/// Applies an agent's knowledge scoping — the retrieval counterpart of
/// <see cref="AgentToolSelection"/>. An agent lists patterns over a collection's canonical path
/// <c>{moduleId}/{resourceType|-}/{name}</c>; retrieval intersects them with the collections the
/// caller's RBAC and collection gates already allow. It can only narrow, so scoping is a
/// composition surface (build "the Spanish employment-law assistant") and never a
/// privilege-escalation one.
/// </summary>
public static class AgentCollectionSelection
{
    /// <summary>The separator between path segments — chosen because it cannot occur in a module id.</summary>
    public const char Separator = '/';

    /// <summary>Stands in for "not bound to a resource" so unbound collections still have three segments.</summary>
    public const string NoResourceType = "-";

    /// <summary>True when the selection is absent/empty — meaning "every collection already allowed".</summary>
    public static bool AllowsAll(IReadOnlyCollection<string>? patterns) =>
        patterns is null || patterns.Count == 0;

    /// <summary>
    /// The path a pattern matches against. Case is preserved for readability; matching is
    /// case-insensitive because collection names are user-typed.
    /// </summary>
    public static string Path(string moduleId, string? resourceType, string name) =>
        $"{moduleId}{Separator}{(string.IsNullOrEmpty(resourceType) ? NoResourceType : resourceType)}{Separator}{name}";

    public static bool Matches(IReadOnlyCollection<string>? patterns, string moduleId, string? resourceType, string name) =>
        AllowsAll(patterns) || MatchesPath(patterns, Path(moduleId, resourceType, name));

    public static bool MatchesPath(IReadOnlyCollection<string>? patterns, string path)
    {
        if (AllowsAll(patterns))
        {
            return true;
        }

        foreach (var pattern in patterns!)
        {
            if (!string.IsNullOrWhiteSpace(pattern) && IsMatch(pattern.Trim(), path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Glob match where <c>*</c> stands for any run of characters, including separators — so
    /// <c>legal/*</c> covers every legal collection and <c>*/matter/*</c> every matter-bound one.
    /// Iterative two-pointer backtracking: no regex, no compilation, no catastrophic backtracking.
    /// </summary>
    private static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        int p = 0, v = 0, starAt = -1, matchAt = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '*'))
            {
                starAt = p++;
                matchAt = v;
            }
            else if (p < pattern.Length && Same(pattern[p], value[v]))
            {
                p++;
                v++;
            }
            else if (starAt >= 0)
            {
                // Backtrack: let the last '*' swallow one more character.
                p = starAt + 1;
                v = ++matchAt;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
