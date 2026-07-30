using Plenipo.Application.Authorization;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>
/// Covers role → permission resolution under deviation storage: a role grants the declared baseline
/// minus what the tenant suppressed plus what it granted, a suppression is permanent, a role a tenant
/// never touched still tracks the baseline no matter what OTHER roles it edited, and system_admin always
/// holds the global wildcard regardless of stored rows.
/// </summary>
public sealed class RolePermissionResolutionTests
{
    private static Dictionary<string, IReadOnlyList<string>> Rows(params (string Role, string[] Permissions)[] rows) =>
        rows.ToDictionary(r => r.Role, r => (IReadOnlyList<string>)r.Permissions, StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyList<string>> None() => new(StringComparer.Ordinal);

    private static IReadOnlySet<string> Resolve(
        string[] roles,
        Dictionary<string, IReadOnlyList<string>>? granted = null,
        Dictionary<string, IReadOnlyList<string>>? suppressed = null,
        IReadOnlyDictionary<string, string[]>? baseline = null) =>
        RolePermissionResolution.PermissionsForRoles(
            roles, granted ?? None(), suppressed ?? None(), baseline ?? RolePermissions.Defaults);

    [Fact]
    public void NoDeviations_GrantsTheBaseline()
    {
        var permissions = Resolve([Roles.User]);

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
    }

    [Fact]
    public void GrantedRows_AddToTheBaseline()
    {
        // The tenant granted `user` a capability the baseline doesn't include. The baseline still applies.
        var permissions = Resolve([Roles.User], granted: Rows((Roles.User, [Permissions.ManageApprovals])));

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
    }

    [Fact]
    public void SuppressedPermission_IsNotGranted()
    {
        // Acceptance (b) of #72 at the unit level: an admin's removal is explicit and permanent.
        var permissions = Resolve(
            [Roles.User], suppressed: Rows((Roles.User, [Permissions.ViewConversations])));

        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ViewConversations));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
    }

    [Fact]
    public void RoleSuppressedEntirely_GrantsNothing()
    {
        // An admin can still deliberately empty a role — it just has to be said, not inferred from the
        // absence of rows. (Replaces the old ConfiguredTenant_RoleWithNoRows_GrantsNothing, which
        // expressed the same intent through storage that could not distinguish it from "never declared".)
        var permissions = Resolve(
            [Roles.Guest], suppressed: Rows((Roles.Guest, RolePermissions.Defaults[Roles.Guest])));

        Assert.Empty(permissions);
    }

    [Fact]
    public void EditingOneRole_LeavesAnotherTrackingTheBaseline()
    {
        // The defect behind #72: under the old model any row at all made the tenant authoritative, so
        // `guest` — untouched — resolved to nothing the moment `user` was edited.
        var permissions = Resolve(
            [Roles.Guest],
            granted: Rows((Roles.User, [Permissions.ManageApprovals])),
            suppressed: Rows((Roles.User, [Permissions.ViewConversations])));

        Assert.True(PermissionMatcher.IsGranted(permissions, RolePermissions.Defaults[Roles.Guest][0]));
    }

    [Fact]
    public void NewlyDeclaredRole_OnATenantWithDeviations_GrantsItsBaseline()
    {
        // Acceptance (c): a ProductRole declared AFTER the tenant was seeded must not grant nothing.
        var baseline = RoleBaseline.Merge([new ProductRole { Role = "paralegal", Permissions = ["chat.use", "legal.matters.view"] }]);

        var permissions = Resolve(
            ["paralegal"],
            granted: Rows((Roles.User, [Permissions.ManageApprovals])),
            baseline: baseline);

        Assert.True(PermissionMatcher.IsGranted(permissions, "legal.matters.view"));
    }

    [Fact]
    public void NewlyDeclaredPermission_ReachesARoleTheTenantEdited()
    {
        // Acceptance (a): the product adds a permission to a role whose OTHER permissions the tenant has
        // already deviated from. The addition lands, because it was never suppressed.
        var baseline = RoleBaseline.Merge(
            [new ProductRole { Role = Roles.TenantAdmin, Permissions = [Permissions.ManageApprovals] }]);

        var permissions = Resolve(
            [Roles.TenantAdmin],
            suppressed: Rows((Roles.TenantAdmin, [Permissions.ViewAuditLog])),
            baseline: baseline);

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals));
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ViewAuditLog));
    }

    [Fact]
    public void SystemAdmin_AlwaysHoldsWildcard_EvenWithNoRows()
    {
        var permissions = Resolve([Roles.SystemAdmin]);

        Assert.Contains("*", permissions);
        Assert.True(PermissionMatcher.IsGranted(permissions, "tools.anything.at_all"));
    }

    [Fact]
    public void SystemAdmin_WildcardNotRemovable_EvenIfEverythingIsSuppressed()
    {
        // A (rejected by the API, but defend in depth) attempt to strip system_admin must not impotent it.
        var permissions = Resolve(
            [Roles.SystemAdmin], suppressed: Rows((Roles.SystemAdmin, ["*"])));

        Assert.Contains("*", permissions);
    }

    [Fact]
    public void MultipleRoles_AreUnioned()
    {
        var permissions = Resolve(
            [Roles.User, Roles.TenantAdmin], granted: Rows((Roles.TenantAdmin, ["platform.*"])));

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
    }
}
