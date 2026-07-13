using Plenipo.Application.Authorization;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>
/// Covers the configurable role → permission resolution: a tenant's stored rows override the built-in
/// defaults, an unseeded tenant falls back to defaults, an explicitly-emptied role grants nothing, and
/// system_admin always holds the global wildcard regardless of configuration.
/// </summary>
public sealed class RolePermissionResolutionTests
{
    private static Dictionary<string, IReadOnlyList<string>> Config(params (string Role, string[] Permissions)[] rows) =>
        rows.ToDictionary(r => r.Role, r => (IReadOnlyList<string>)r.Permissions, StringComparer.Ordinal);

    [Fact]
    public void NoConfiguration_FallsBackToDefaults()
    {
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.User], empty);

        // Same as the built-in default for `user`.
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
    }

    [Fact]
    public void ConfiguredRows_OverrideDefaults()
    {
        // Tenant has granted `user` an extra capability the default doesn't include.
        var config = Config((Roles.User, [Permissions.UseChat, Permissions.ManageApprovals]));

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.User], config);

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals));
    }

    [Fact]
    public void ConfiguredTenant_RoleWithNoRows_GrantsNothing()
    {
        // The tenant IS configured (it has rows for `user`), but `guest` was deliberately emptied — so it
        // must grant nothing, NOT silently fall back to the default.
        var config = Config((Roles.User, [Permissions.UseChat]));

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.Guest], config);

        Assert.Empty(permissions);
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ViewConversations));
    }

    [Fact]
    public void SystemAdmin_AlwaysHoldsWildcard_EvenWhenUnconfigured()
    {
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.SystemAdmin], empty);

        Assert.Contains("*", permissions);
        Assert.True(PermissionMatcher.IsGranted(permissions, "tools.anything.at_all"));
    }

    [Fact]
    public void SystemAdmin_WildcardNotRemovable_EvenIfConfiguredEmpty()
    {
        // A (rejected by the API, but defend in depth) attempt to strip system_admin must not impotent it.
        var config = Config((Roles.User, [Permissions.UseChat]), (Roles.SystemAdmin, []));

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.SystemAdmin], config);

        Assert.Contains("*", permissions);
    }

    [Fact]
    public void MultipleRoles_AreUnioned()
    {
        var config = Config(
            (Roles.User, [Permissions.UseChat]),
            (Roles.TenantAdmin, ["platform.*"]));

        var permissions = RolePermissionResolution.PermissionsForRoles([Roles.User, Roles.TenantAdmin], config);

        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
    }
}
