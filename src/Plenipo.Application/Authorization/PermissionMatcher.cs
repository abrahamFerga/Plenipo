namespace Plenipo.Application.Authorization;

/// <summary>
/// Decides whether a set of granted permissions satisfies a required permission, honouring
/// <c>system_admin</c> (holds everything) and dotted wildcard grants (<c>tools.finance.*</c> covers
/// <c>tools.finance.categorize</c>).
/// </summary>
public static class PermissionMatcher
{
    public static bool IsGranted(IReadOnlySet<string> granted, string required)
    {
        ArgumentNullException.ThrowIfNull(granted);
        ArgumentException.ThrowIfNullOrWhiteSpace(required);

        // Global wildcard (held by system_admin) satisfies everything.
        if (granted.Contains("*") || granted.Contains(required))
        {
            return true;
        }

        // Walk up the dotted hierarchy looking for a wildcard grant: a.b.c -> a.b.* -> a.*
        var lastDot = required.LastIndexOf('.');
        while (lastDot > 0)
        {
            if (granted.Contains(string.Concat(required.AsSpan(0, lastDot + 1), "*")))
            {
                return true;
            }

            lastDot = required.LastIndexOf('.', lastDot - 1);
        }

        return false;
    }
}
