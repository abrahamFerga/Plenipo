using Plenipo.Application.Authorization;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>
/// Covers <see cref="PermissionGrantValidator"/> — the guard that stops an admin from assigning a role or user
/// a permission string that matches nothing in the deployment (a typo silently grants nothing). Concrete
/// permissions and meaningful wildcards pass; strings that cover no real permission are reported.
/// </summary>
public sealed class PermissionGrantValidatorTests
{
    // Platform permissions + two module tools from a hypothetical "finance" module.
    private static readonly IReadOnlySet<string> Known = PermissionGrantValidator.KnownPermissions(
        ["tools.finance.summarize_spending", "tools.finance.categorize"]);

    [Fact]
    public void KnownPermissions_MergesPlatformPermissionsAndModuleTools()
    {
        Assert.Contains(Permissions.ManageUsers, Known);              // a built-in platform permission
        Assert.Contains("tools.finance.categorize", Known);          // a supplied module tool
    }

    [Fact]
    public void ConcretePlatformPermission_IsMeaningful()
    {
        Assert.Empty(PermissionGrantValidator.FindUnknownGrants([Permissions.ManageUsers], Known));
    }

    [Fact]
    public void ConcreteModuleToolPermission_IsMeaningful()
    {
        Assert.Empty(PermissionGrantValidator.FindUnknownGrants(["tools.finance.categorize"], Known));
    }

    [Theory]
    [InlineData("tools.finance.*")]   // module wildcard covering real tools
    [InlineData("chat.*")]            // platform sub-tree wildcard covering chat.* permissions
    [InlineData("*")]                 // global wildcard
    public void WildcardCoveringAtLeastOneKnownPermission_IsMeaningful(string grant)
    {
        Assert.Empty(PermissionGrantValidator.FindUnknownGrants([grant], Known));
    }

    [Fact]
    public void TypoedConcretePermission_IsReportedUnknown()
    {
        var unknown = PermissionGrantValidator.FindUnknownGrants(["platform.users.mange"], Known);

        Assert.Equal(["platform.users.mange"], unknown);
    }

    [Fact]
    public void WildcardCoveringNothing_IsReportedUnknown()
    {
        var unknown = PermissionGrantValidator.FindUnknownGrants(["tools.zzz.*"], Known);

        Assert.Equal(["tools.zzz.*"], unknown);
    }

    [Fact]
    public void MixedGrants_ReportsOnlyTheUnknowns_DeduplicatedAndInOrder()
    {
        var unknown = PermissionGrantValidator.FindUnknownGrants(
            [Permissions.ManageUsers, "bogus.one", "tools.finance.*", "bogus.one", "bogus.two"], Known);

        Assert.Equal(["bogus.one", "bogus.two"], unknown);
    }

    [Fact]
    public void BlankGrants_AreIgnored()
    {
        Assert.Empty(PermissionGrantValidator.FindUnknownGrants(["", "   ", Permissions.UseChat], Known));
    }

    // ── Operator-only escalation guard (FindForbiddenGrants) ──────────────────
    // A tenant admin holds tenant-scoped platform permissions but NOT cross-tenant management; the operator
    // (system_admin) holds everything.
    private static bool TenantAdminHolds(string permission) =>
        PermissionMatcher.IsGranted(
            new HashSet<string>(RolePermissions.ForRole(Roles.TenantAdmin), StringComparer.Ordinal), permission);

    private static bool OperatorHolds(string permission) => true;

    [Fact]
    public void TenantAdmin_CannotGrant_OperatorOnlyPermission_NamedDirectly()
    {
        var forbidden = PermissionGrantValidator.FindForbiddenGrants(
            [Permissions.ManageTenants], Permissions.OperatorOnly, TenantAdminHolds);

        Assert.Equal([Permissions.ManageTenants], forbidden);
    }

    [Theory]
    [InlineData("platform.*")]  // covers platform.tenants.manage
    [InlineData("*")]           // covers everything
    public void TenantAdmin_CannotGrant_AWildcardCoveringAnOperatorOnlyPermission(string grant)
    {
        var forbidden = PermissionGrantValidator.FindForbiddenGrants(
            [grant], Permissions.OperatorOnly, TenantAdminHolds);

        Assert.Equal([grant], forbidden);
    }

    [Fact]
    public void TenantAdmin_CanGrant_ItsOwnTenantScopedPermissionsAndModuleTools()
    {
        var forbidden = PermissionGrantValidator.FindForbiddenGrants(
            [Permissions.ManageUsers, Permissions.ManageRoles, "platform.users.*", "chat.*", "tools.finance.*"],
            Permissions.OperatorOnly, TenantAdminHolds);

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Operator_CanGrant_OperatorOnlyPermissions_ToDelegate()
    {
        // The operator already holds the reserved capability, so it may hand it out (delegation).
        var forbidden = PermissionGrantValidator.FindForbiddenGrants(
            [Permissions.ManageTenants, "platform.*"], Permissions.OperatorOnly, OperatorHolds);

        Assert.Empty(forbidden);
    }
}
