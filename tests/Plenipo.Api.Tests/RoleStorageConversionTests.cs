using Plenipo.Application.Authorization;
using Plenipo.AspNetCore.Setup;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The upgrade contract for issue #72: converting a tenant from legacy full-set role storage to deviation
/// storage must change NOBODY's effective permissions. That is the whole reason the conversion is safe to
/// run unattended against every existing deployment, and it is the assertion a careless fix breaks — an
/// add-only reconciler would resurrect permissions an admin deliberately removed.
/// </summary>
public sealed class RoleStorageConversionTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public RoleStorageConversionTests(PlenipoApiFactory factory) => _factory = factory;

    private static readonly IReadOnlyDictionary<string, string[]> Baseline = RolePermissions.Defaults;

    /// <summary>Creates an UNCONVERTED tenant holding legacy full-set rows, exactly as the old seed wrote them.</summary>
    private async Task<Guid> SeedLegacyTenantAsync(
        PlatformDbContext db, string slug, params (string Role, string[] Permissions)[] rows)
    {
        var tenant = new Tenant { Name = slug, Slug = slug, RolePermissionsConvertedAt = null };
        db.Tenants.Add(tenant);
        foreach (var (role, permissions) in rows)
        {
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission { TenantId = tenant.Id, Role = role, Permission = permission });
            }
        }

        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>What a principal holding <paramref name="role"/> resolves to, read straight from storage.</summary>
    private static async Task<IReadOnlySet<string>> EffectiveAsync(PlatformDbContext db, Guid tenantId, string role)
    {
        var granted = await db.RolePermissions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId).Select(r => new { r.Role, r.Permission }).ToListAsync();
        var suppressed = await db.RolePermissionSuppressions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId).Select(r => new { r.Role, r.Permission }).ToListAsync();

        return RolePermissionResolution.PermissionsForRoles(
            [role],
            granted.GroupBy(r => r.Role, StringComparer.Ordinal).ToDictionary(
                g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Permission).ToList(), StringComparer.Ordinal),
            suppressed.GroupBy(r => r.Role, StringComparer.Ordinal).ToDictionary(
                g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Permission).ToList(), StringComparer.Ordinal),
            Baseline);
    }

    /// <summary>The LEGACY resolution: any row for the tenant is authoritative, a role with no rows grants nothing.</summary>
    private static IReadOnlySet<string> LegacyEffective(
        IReadOnlyDictionary<string, string[]> rows, string role)
    {
        if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
        {
            return new HashSet<string>(StringComparer.Ordinal) { "*" };
        }

        return new HashSet<string>(
            rows.Count > 0 ? rows.GetValueOrDefault(role, []) : Baseline.GetValueOrDefault(role, []),
            StringComparer.Ordinal);
    }

    private async Task<(PlatformDbContext Db, IServiceScope Scope)> ScopeAsync()
    {
        _ = _factory.CreateClient(); // forces startup: migrations + dev seeding + the conversion pass
        var scope = _factory.Services.CreateScope();
        return await Task.FromResult((scope.ServiceProvider.GetRequiredService<PlatformDbContext>(), scope));
    }

    [Fact]
    public async Task Conversion_preserves_effective_permissions_exactly()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        // A legacy tenant that (a) had a baseline permission REMOVED from `user`, (b) had a non-baseline
        // permission ADDED to `user`, and (c) has zero rows for `guest` — which under the old model meant
        // "grants nothing", not "tracks the baseline".
        var userRows = Baseline[Roles.User].Where(p => p != Permissions.ViewConversations)
            .Append(Permissions.ManageApprovals).ToArray();
        var legacyRows = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [Roles.User] = userRows,
            [Roles.TenantAdmin] = Baseline[Roles.TenantAdmin],
        };
        var tenantId = await SeedLegacyTenantAsync(db, "legacy-exact", (Roles.User, userRows), (Roles.TenantAdmin, Baseline[Roles.TenantAdmin]));

        var before = new[] { Roles.User, Roles.Guest, Roles.TenantAdmin, Roles.SystemAdmin }
            .ToDictionary(r => r, r => LegacyEffective(legacyRows, r), StringComparer.Ordinal);

        await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);

        foreach (var (role, expected) in before)
        {
            var actual = await EffectiveAsync(db, tenantId, role);
            Assert.Equal(expected.OrderBy(p => p, StringComparer.Ordinal), actual.OrderBy(p => p, StringComparer.Ordinal));
        }

        // And specifically: the permission the admin removed is now an explicit suppression, not an absence.
        Assert.True(await db.RolePermissionSuppressions.IgnoreQueryFilters().AnyAsync(
            r => r.TenantId == tenantId && r.Role == Roles.User && r.Permission == Permissions.ViewConversations));
    }

    [Fact]
    public async Task Conversion_is_idempotent()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        var tenantId = await SeedLegacyTenantAsync(db, "legacy-idempotent", (Roles.User, [Permissions.UseChat]));

        await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);
        var grantsAfterFirst = await db.RolePermissions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId);
        var suppressionsAfterFirst = await db.RolePermissionSuppressions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId);

        var secondPass = await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);

        Assert.Equal(0, secondPass);
        Assert.Equal(grantsAfterFirst, await db.RolePermissions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
        Assert.Equal(suppressionsAfterFirst, await db.RolePermissionSuppressions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
    }

    [Fact]
    public async Task A_rowless_tenant_converts_to_zero_deviations()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        var tenantId = await SeedLegacyTenantAsync(db, "legacy-rowless");

        await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);

        Assert.Equal(0, await db.RolePermissions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
        Assert.Equal(0, await db.RolePermissionSuppressions.IgnoreQueryFilters().CountAsync(r => r.TenantId == tenantId));
        Assert.NotNull((await db.Tenants.FirstAsync(t => t.Id == tenantId)).RolePermissionsConvertedAt);

        // It tracks the baseline — which is what it resolved to before, via the unseeded fallback.
        Assert.Equal(
            Baseline[Roles.User].OrderBy(p => p, StringComparer.Ordinal),
            (await EffectiveAsync(db, tenantId, Roles.User)).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task System_admin_rows_are_removed_but_the_wildcard_survives()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        var tenantId = await SeedLegacyTenantAsync(
            db, "legacy-sysadmin", (Roles.SystemAdmin, ["*"]), (Roles.User, [Permissions.UseChat]));

        await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);

        Assert.False(await db.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.Role == Roles.SystemAdmin));
        Assert.False(await db.RolePermissionSuppressions.IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.Role == Roles.SystemAdmin));
        Assert.Contains("*", await EffectiveAsync(db, tenantId, Roles.SystemAdmin));
    }

    [Fact]
    public async Task A_custom_role_keeps_every_row_and_gains_no_suppressions()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        var tenantId = await SeedLegacyTenantAsync(
            db, "legacy-custom", (Roles.User, Baseline[Roles.User]), ("paralegal", ["chat.use", "legal.matters.view"]));

        await RoleStorageConversion.ConvertAsync(db, Baseline, NullLogger.Instance);

        var custom = await db.RolePermissions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.Role == "paralegal").Select(r => r.Permission).ToListAsync();
        Assert.Equal(["chat.use", "legal.matters.view"], custom.OrderBy(p => p, StringComparer.Ordinal));
        Assert.False(await db.RolePermissionSuppressions.IgnoreQueryFilters()
            .AnyAsync(r => r.TenantId == tenantId && r.Role == "paralegal"));
    }

    [Fact]
    public async Task A_role_declared_after_seeding_keeps_granting_nothing_until_it_is_restored()
    {
        var (db, scope) = await ScopeAsync();
        using var _ = scope;

        // The tenant is configured (rows for `user`) but has none for a role the product declares. Under the
        // old model that role granted nothing, and the conversion must preserve that rather than guess: an
        // upgrade that silently hands out permissions is an RBAC violation across every tenant at once.
        var baseline = RoleBaseline.Merge([new ProductRole { Role = "paralegal", Permissions = ["chat.use"] }]);
        var tenantId = await SeedLegacyTenantAsync(db, "legacy-declared-later", (Roles.User, Baseline[Roles.User]));

        await RoleStorageConversion.ConvertAsync(db, baseline, NullLogger.Instance);

        var suppressed = await db.RolePermissionSuppressions.IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.Role == "paralegal").Select(r => r.Permission).ToListAsync();
        Assert.Equal(["chat.use"], suppressed);
    }
}
