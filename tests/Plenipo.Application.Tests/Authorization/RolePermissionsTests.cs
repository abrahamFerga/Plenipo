using Plenipo.Application.Authorization;

namespace Plenipo.Application.Tests.Authorization;

public sealed class RolePermissionsTests
{
    [Fact]
    public void SystemAdmin_HoldsGlobalWildcard()
    {
        var permissions = RolePermissions.ForRole(Roles.SystemAdmin).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("*", permissions);
        // Therefore, through the matcher, system_admin satisfies any permission.
        Assert.True(PermissionMatcher.IsGranted(permissions, "tools.anything.at_all"));
    }

    [Fact]
    public void User_CanChatButNotAdminister()
    {
        var permissions = RolePermissions.ForRole(Roles.User).ToHashSet(StringComparer.Ordinal);
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
        // A regular user may chat but may NOT approve side-effecting tool calls (human-in-the-loop gate).
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals));
    }

    [Fact]
    public void TenantAdmin_AdministersOwnTenantAndChats_ButCannotManageOtherTenants()
    {
        var permissions = RolePermissions.ForRole(Roles.TenantAdmin).ToHashSet(StringComparer.Ordinal);
        // Every tenant-scoped platform capability it needs to run its own tenant.
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageUsers));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageRoles));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageModules));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageAiSettings));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ViewAuditLog));
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.UseChat));
        // The chat.* baseline covers approving side-effecting actions.
        Assert.True(PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals));
        // ...but NOT cross-tenant management — that is operator-only (the multi-tenant isolation guarantee).
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ManageTenants));
        // The former platform.* baseline would have covered it — guard against re-broadening.
        Assert.DoesNotContain("platform.*", permissions);
        Assert.DoesNotContain("*", permissions);
        // ...and not an arbitrary module tool unless explicitly granted.
        Assert.False(PermissionMatcher.IsGranted(permissions, Permissions.ForTool("finance", "categorize_transaction")));
    }

    [Fact]
    public void UnknownRole_GrantsNothing()
    {
        Assert.Empty(RolePermissions.ForRole("not_a_role"));
    }
}
