namespace Plenipo.Application.Authorization;

/// <summary>
/// Pure logic for turning a set of roles into the permissions they grant, given a tenant's <em>configured</em>
/// role → permission rows. Kept free of any database or HTTP concern so it can be unit-tested in isolation;
/// <c>PermissionResolver</c> in the infrastructure layer fetches the rows and calls this.
///
/// Rules:
/// <list type="bullet">
///   <item>If the tenant has <b>any</b> configured rows, they are authoritative — a role with no rows grants
///   nothing (so an admin can deliberately empty a role).</item>
///   <item>If the tenant has <b>no</b> configured rows at all (never seeded — e.g. a legacy or anonymous
///   context), fall back to the built-in <see cref="RolePermissions.Defaults"/>, preserving prior behaviour.</item>
///   <item><c>system_admin</c> always holds the global wildcard <c>*</c>, regardless of configuration — a
///   lockout guardrail so the role can never be edited (or left unseeded) into impotence.</item>
/// </list>
/// </summary>
public static class RolePermissionResolution
{
    /// <summary>
    /// Computes the permissions granted by <paramref name="roles"/> under a tenant's configuration.
    /// </summary>
    /// <param name="roles">The roles held by the principal (from token claims and/or DB assignments).</param>
    /// <param name="configuredByRole">
    /// The tenant's configured role → permissions, grouped by role. Empty when the tenant has never been
    /// seeded, which triggers the built-in-defaults fallback.
    /// </param>
    public static IReadOnlySet<string> PermissionsForRoles(
        IEnumerable<string> roles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> configuredByRole) =>
        PermissionsForRoles(roles, configuredByRole, RolePermissions.Defaults);

    /// <summary>
    /// As above, with an explicit fallback baseline — the built-ins merged with any host-declared
    /// <see cref="ProductRole"/>s (see <see cref="RoleBaseline.Merge"/>).
    /// </summary>
    public static IReadOnlySet<string> PermissionsForRoles(
        IEnumerable<string> roles,
        IReadOnlyDictionary<string, IReadOnlyList<string>> configuredByRole,
        IReadOnlyDictionary<string, string[]> fallbackByRole)
    {
        ArgumentNullException.ThrowIfNull(fallbackByRole);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(configuredByRole);

        var tenantHasConfiguration = configuredByRole.Count > 0;
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var role in roles)
        {
            // Guardrail: system_admin is omnipotent by construction and can never be configured away.
            if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
            {
                result.Add("*");
                continue;
            }

            if (tenantHasConfiguration)
            {
                if (configuredByRole.TryGetValue(role, out var configured))
                {
                    result.UnionWith(configured);
                }
                // else: a known role with no rows in a configured tenant grants nothing.
            }
            else
            {
                result.UnionWith(fallbackByRole.TryGetValue(role, out var fallback) ? fallback : []);
            }
        }

        return result;
    }
}
