namespace Plenipo.Modules.Legal;

/// <summary>
/// The conflict check's matching rule, factored pure for tests. Deliberately LOOSE and recall-
/// biased: a conflicts search must surface candidates for a human to clear, never quietly miss
/// one. A party matches when its name appears inside the query text or vice versa (so both
/// "Initech" vs "Initech Corporation" and a whole intake sentence containing the name hit),
/// case-insensitive, ignoring trivially short fragments.
/// </summary>
public static class ConflictCheck
{
    /// <summary>Fragments shorter than this never match — "Al" must not hit half the database.</summary>
    public const int MinimumLength = 3;

    public static bool Matches(string query, string partyName)
    {
        var q = query.Trim();
        var p = partyName.Trim();
        if (q.Length < MinimumLength || p.Length < MinimumLength)
        {
            return false;
        }

        return q.Contains(p, StringComparison.OrdinalIgnoreCase) ||
               p.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
