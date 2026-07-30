namespace Plenipo.Application.Authorization;

/// <summary>
/// The single canonical answer to "what does role R grant in this tenant".
///
/// Deviation model: a tenant stores only what it CHANGED, never the whole set.
/// <c>effective = (baseline ∖ suppressed) ∪ granted</c>.
///
/// This is what lets a product's declared baseline reach every tenant — including tenants
/// provisioned long before the declaration changed — with no reconciler, no backfill and no restart
/// pass, while an admin's removal stays an explicit, permanent row. A custom (tenant-defined) role
/// has no baseline entry, so it is exactly its granted rows.
/// </summary>
public static class RoleGrants
{
    /// <summary>The permissions <paramref name="role"/> grants, given a tenant's deviations.</summary>
    /// <param name="baselineByRole">The declared baseline — built-ins merged with host <see cref="ProductRole"/>s.</param>
    /// <param name="grantedByRole">Permissions this tenant granted BEYOND the baseline (plus all of a custom role).</param>
    /// <param name="suppressedByRole">Baseline permissions this tenant removed.</param>
    public static IReadOnlyList<string> Effective(
        string role,
        IReadOnlyDictionary<string, string[]> baselineByRole,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grantedByRole,
        IReadOnlyDictionary<string, IReadOnlyList<string>> suppressedByRole)
    {
        ArgumentNullException.ThrowIfNull(baselineByRole);
        ArgumentNullException.ThrowIfNull(grantedByRole);
        ArgumentNullException.ThrowIfNull(suppressedByRole);

        var effective = new HashSet<string>(StringComparer.Ordinal);

        if (baselineByRole.TryGetValue(role, out var baseline))
        {
            effective.UnionWith(baseline);
        }

        // Suppression is applied BEFORE grants: an explicit grant of a permission that is also
        // suppressed wins, which is the only reading that keeps PUT /roles/{role}/permissions
        // round-tripping the exact set an admin submitted.
        if (suppressedByRole.TryGetValue(role, out var suppressed))
        {
            effective.ExceptWith(suppressed);
        }

        if (grantedByRole.TryGetValue(role, out var granted))
        {
            effective.UnionWith(granted);
        }

        return [.. effective];
    }

    /// <summary>
    /// True when the role is one a principal may hold: a built-in, a host-declared
    /// <see cref="ProductRole"/>, or a custom role the tenant defined at runtime.
    ///
    /// Under deviation storage a declared role normally has NO rows at all, so "does a row exist"
    /// is no longer an existence test — every caller that used one must ask this instead.
    /// </summary>
    public static bool IsKnown(
        string role,
        IReadOnlyDictionary<string, string[]> baselineByRole,
        IEnumerable<string> customRolesWithRows)
    {
        ArgumentNullException.ThrowIfNull(baselineByRole);
        ArgumentNullException.ThrowIfNull(customRolesWithRows);

        return Roles.All.Contains(role, StringComparer.Ordinal)
            || baselineByRole.ContainsKey(role)
            || customRolesWithRows.Contains(role, StringComparer.Ordinal);
    }
}
