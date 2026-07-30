using System.Security.Claims;
using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Authorization;

/// <summary>
/// Computes a user's effective permission set by merging three sources: the roles the principal holds
/// (from token claims and/or DB assignments), the user's explicit DB grants, and any fine-grained
/// permission claims. Roles are expanded to permissions from the declared baseline
/// (<see cref="RolePermissions.Defaults"/> merged with the host's <see cref="ProductRole"/>s) adjusted by
/// the tenant's stored <em>deviations</em> — the permissions it granted beyond the baseline and the ones
/// it removed, both editable in the admin console. See <see cref="RolePermissionResolution"/> for the
/// exact rules.
///
/// With <c>Auth:PermissionSource=Token</c> the DB sources drop out entirely: roles come only from
/// the IdP's token, and per-user DB grants are never consulted — the external IdP is the single
/// source of truth for who holds what (the baselines still translate role names into permissions).
/// </summary>
public sealed class PermissionResolver(
    PlatformDbContext db,
    ICurrentUser currentUser,
    IOptions<AuthorizationSourceOptions> authorizationSource,
    IEnumerable<ProductRole> productRoles) : IPermissionResolver
{
    private readonly Lazy<IReadOnlyDictionary<string, string[]>> _baseline =
        new(() => RoleBaseline.Merge(productRoles));

    public async Task<IReadOnlySet<string>> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var tokenSourced = authorizationSource.Value.IsTokenSourced;
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        // 1. Collect every role the principal holds — asserted by the IdP and/or assigned in the DB.
        var roles = new HashSet<string>(StringComparer.Ordinal);
        roles.UnionWith(principal.FindAll(ClaimTypes.Role).Concat(principal.FindAll("roles")).Select(c => c.Value));

        if (!tokenSourced && currentUser.UserId is Guid userId)
        {
            var dbRoles = await db.UserRoles
                .Where(r => r.UserId == userId)
                .Select(r => r.Role)
                .ToListAsync(cancellationToken);
            roles.UnionWith(dbRoles);
        }

        // 2. Expand roles → permissions from the declared baseline, adjusted by this tenant's deviations.
        //    Both reads are scoped to the ambient tenant by the global query filter.
        var grantedByRole = await GroupByRoleAsync(
            db.RolePermissions.Select(r => new RoleRow(r.Role, r.Permission)), cancellationToken);
        var suppressedByRole = await GroupByRoleAsync(
            db.RolePermissionSuppressions.Select(r => new RoleRow(r.Role, r.Permission)), cancellationToken);

        permissions.UnionWith(RolePermissionResolution.PermissionsForRoles(
            roles, grantedByRole, suppressedByRole, _baseline.Value));

        // 3. Explicit per-user grants for the provisioned user (tenant-filtered) — never in Token mode.
        if (!tokenSourced && currentUser.UserId is Guid uid)
        {
            var dbPermissions = await db.UserPermissions
                .Where(p => p.UserId == uid)
                .Select(p => p.Permission)
                .ToListAsync(cancellationToken);
            permissions.UnionWith(dbPermissions);
        }

        // 4. Fine-grained permission claims minted directly into the token.
        permissions.UnionWith(principal.FindAll("permissions").Select(c => c.Value));

        return permissions;
    }

    private sealed record RoleRow(string Role, string Permission);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GroupByRoleAsync(
        IQueryable<RoleRow> rows, CancellationToken cancellationToken) =>
        (await rows.ToListAsync(cancellationToken))
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Permission).ToList(),
                StringComparer.Ordinal);
}
