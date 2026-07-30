namespace Plenipo.Application.Authorization;

/// <summary>
/// Pure logic for turning a set of roles into the permissions they grant, given a tenant's stored
/// <em>deviations</em> from the declared baseline. Kept free of any database or HTTP concern so it can be
/// unit-tested in isolation; <c>PermissionResolver</c> in the infrastructure layer fetches the rows and
/// calls this.
///
/// Rules:
/// <list type="bullet">
///   <item>A role grants <c>(baseline ∖ suppressed) ∪ granted</c>. The baseline is the built-in defaults
///   merged with the host's <see cref="ProductRole"/> declarations (<see cref="RoleBaseline.Merge"/>), so a
///   permission a product declares reaches EVERY tenant immediately — including one provisioned before the
///   declaration changed.</item>
///   <item>A permission a tenant admin removed is stored as an explicit suppression, so it stays removed
///   across every later baseline change. An admin can still deliberately empty a role: suppressing all of
///   its baseline grants nothing.</item>
///   <item><c>system_admin</c> always holds the global wildcard <c>*</c>, regardless of stored rows — a
///   lockout guardrail so the role can never be edited into impotence.</item>
/// </list>
///
/// This deliberately has no tenant-wide "is this tenant configured" switch. The previous storage model
/// materialized every role's full permission set on first seed and treated the presence of ANY row as
/// "this tenant is authoritative", which meant a role with no rows granted nothing — so a declaration
/// change could never reach a seeded tenant, and a per-role write could zero every other role.
/// </summary>
public static class RolePermissionResolution
{
    /// <summary>
    /// Computes the permissions granted by <paramref name="roles"/> under a tenant's deviations.
    /// </summary>
    /// <param name="roles">The roles held by the principal (from token claims and/or DB assignments).</param>
    /// <param name="grantedByRole">Permissions the tenant granted beyond the baseline, grouped by role.</param>
    /// <param name="suppressedByRole">Baseline permissions the tenant removed, grouped by role.</param>
    /// <param name="baselineByRole">The declared baseline: built-ins merged with host <see cref="ProductRole"/>s.</param>
    public static IReadOnlySet<string> PermissionsForRoles(
        IEnumerable<string> roles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> grantedByRole,
        IReadOnlyDictionary<string, IReadOnlyList<string>> suppressedByRole,
        IReadOnlyDictionary<string, string[]> baselineByRole)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(grantedByRole);
        ArgumentNullException.ThrowIfNull(suppressedByRole);
        ArgumentNullException.ThrowIfNull(baselineByRole);

        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            // Guardrail: system_admin is omnipotent by construction and can never be configured away.
            if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
            {
                result.Add("*");
                continue;
            }

            result.UnionWith(RoleGrants.Effective(role, baselineByRole, grantedByRole, suppressedByRole));
        }

        return result;
    }
}
