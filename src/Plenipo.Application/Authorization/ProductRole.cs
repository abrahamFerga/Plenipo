using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Application.Authorization;

/// <summary>
/// A host-declared role baseline: a NEW product role (e.g. "paralegal") or a reshape of a
/// built-in role's default permissions.
///
/// <para>The declaration is LIVE, not a seed. What a role grants is
/// <c>(baseline ∖ suppressed) ∪ granted</c>: this baseline, minus whatever a tenant admin removed, plus
/// whatever they added. Add a permission here in a later release and it reaches every existing tenant on
/// restart — no migration, no reconciler, no per-tenant repair — while a permission an admin deliberately
/// removed stays removed. See <c>RoleGrants</c>.</para>
/// </summary>
public sealed record ProductRole
{
    public required string Role { get; init; }

    public required IReadOnlyList<string> Permissions { get; init; }

    /// <summary>
    /// For a built-in role: true REPLACES its default baseline; false (default) UNIONS with it.
    /// Meaningless for new roles.
    /// </summary>
    public bool Replace { get; init; }
}

/// <summary>Builds the effective role → baseline map: the built-ins with product roles merged over them.</summary>
public static class RoleBaseline
{
    public static IReadOnlyDictionary<string, string[]> Merge(IEnumerable<ProductRole> productRoles)
    {
        var merged = RolePermissions.Defaults.ToDictionary(
            kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        foreach (var role in productRoles)
        {
            if (string.Equals(role.Role, Roles.SystemAdmin, StringComparison.Ordinal))
            {
                // The omnipotence guardrail extends to registration: nobody reshapes system_admin.
                throw new InvalidOperationException("system_admin cannot be customized.");
            }

            merged[role.Role] = role.Replace || !merged.TryGetValue(role.Role, out var existing)
                ? [.. role.Permissions.Distinct(StringComparer.Ordinal)]
                : [.. existing.Concat(role.Permissions).Distinct(StringComparer.Ordinal)];
        }

        return merged;
    }
}

public static class ProductRoleRegistration
{
    /// <summary>
    /// Declares a product role's baseline (or reshapes a built-in's — <paramref name="replace"/>
    /// true replaces the default set, false adds to it). One call per role, next to the host's
    /// other AddPlenipo* calls. system_admin is not customizable.
    ///
    /// <para>Changing the permission list in a later release is the supported way to ship a permission
    /// fix: it applies to every tenant on restart, including tenants provisioned before the change.</para>
    /// </summary>
    public static IServiceCollection AddPlenipoRole(
        this IServiceCollection services, string role, IReadOnlyList<string> permissions, bool replace = false)
    {
        if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("system_admin cannot be customized.");
        }

        services.AddSingleton(new ProductRole { Role = role, Permissions = permissions, Replace = replace });
        return services;
    }
}
