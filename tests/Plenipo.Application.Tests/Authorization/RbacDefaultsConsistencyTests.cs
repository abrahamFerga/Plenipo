using Plenipo.Application.Authorization;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>
/// Guards the built-in permission catalog and the role → permission defaults: the catalog is well-formed,
/// and each built-in role's baseline matches its documented intent (system_admin = everything, user = chat
/// only, guest = read-only). Pins these so an accidental edit can't malform a permission or silently
/// broaden a low-privilege role.
/// </summary>
public sealed class RbacDefaultsConsistencyTests
{
    private static IReadOnlySet<string> GrantsFor(string role) =>
        new HashSet<string>(RolePermissions.ForRole(role), StringComparer.Ordinal);

    [Fact]
    public void Catalog_HasNoDuplicatePermissions()
    {
        var all = PermissionCatalog.Platform.Select(p => p.Permission).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalog_PermissionsAreWellFormed()
    {
        foreach (var info in PermissionCatalog.Platform)
        {
            Assert.DoesNotContain(" ", info.Permission);                        // no whitespace
            Assert.Contains(".", info.Permission);                              // dotted / hierarchical
            Assert.Equal(info.Permission.ToLowerInvariant(), info.Permission);  // lowercase convention
            Assert.False(string.IsNullOrWhiteSpace(info.Category));
            Assert.False(string.IsNullOrWhiteSpace(info.Description));
        }
    }

    [Fact]
    public void SystemAdmin_HoldsTheGlobalWildcard()
    {
        var grants = GrantsFor(Roles.SystemAdmin);
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.ManageTenants));
        Assert.True(PermissionMatcher.IsGranted(grants, "any.future.permission"));
    }

    [Fact]
    public void User_CanChatButIsNotAPlatformAdmin()
    {
        var grants = GrantsFor(Roles.User);
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.UseChat));
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.ViewConversations));
        // A plain user must not, by role alone, get platform administration or the HITL approval power.
        Assert.False(PermissionMatcher.IsGranted(grants, Permissions.ManageUsers));
        Assert.False(PermissionMatcher.IsGranted(grants, Permissions.ManageApprovals));
    }

    [Fact]
    public void Guest_IsReadOnly()
    {
        var grants = GrantsFor(Roles.Guest);
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.ViewConversations));
        Assert.False(PermissionMatcher.IsGranted(grants, Permissions.UseChat)); // can read history, not start chats
        Assert.False(PermissionMatcher.IsGranted(grants, Permissions.ManageUsers));
    }

    [Fact]
    public void TenantAdmin_AdministersItsOwnTenant_ButNotOthers()
    {
        var grants = GrantsFor(Roles.TenantAdmin);
        // Runs its own tenant end to end...
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.ManageUsers));
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.ManageRoles));
        Assert.True(PermissionMatcher.IsGranted(grants, Permissions.UseChat));
        // ...but cross-tenant management is reserved for the operator — the multi-tenant isolation guarantee.
        Assert.False(PermissionMatcher.IsGranted(grants, Permissions.ManageTenants));
    }

    [Fact]
    public void OperatorOnlyPermissions_AreRealAndHeldOnlyByTheOperator()
    {
        var catalog = PermissionCatalog.Platform.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);
        var operatorGrants = GrantsFor(Roles.SystemAdmin);

        Assert.NotEmpty(Permissions.OperatorOnly);
        foreach (var reserved in Permissions.OperatorOnly)
        {
            Assert.Contains(reserved, catalog);                                      // it's a real, catalogued permission
            Assert.True(PermissionMatcher.IsGranted(operatorGrants, reserved));       // the operator holds it
            // No lower-privilege built-in role may hold an operator-reserved permission by default.
            Assert.False(PermissionMatcher.IsGranted(GrantsFor(Roles.TenantAdmin), reserved));
            Assert.False(PermissionMatcher.IsGranted(GrantsFor(Roles.User), reserved));
            Assert.False(PermissionMatcher.IsGranted(GrantsFor(Roles.Guest), reserved));
        }
    }
}
