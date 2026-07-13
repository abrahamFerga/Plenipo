using Plenipo.Application.Authorization;
using Xunit;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>Host-declared role baselines merge over the built-ins (improvement loop it5).</summary>
public sealed class RoleBaselineTests
{
    [Fact]
    public void NewProductRole_JoinsTheBaseline()
    {
        var merged = RoleBaseline.Merge([new ProductRole { Role = "paralegal", Permissions = ["chat.use", "legal.matters.view"] }]);

        Assert.Equal(["chat.use", "legal.matters.view"], merged["paralegal"]);
        Assert.Equal(RolePermissions.Defaults[Roles.User], merged[Roles.User]); // built-ins untouched
    }

    [Fact]
    public void BuiltInRole_UnionsByDefault_ReplacesWhenAsked()
    {
        var unioned = RoleBaseline.Merge([new ProductRole { Role = Roles.Guest, Permissions = ["chat.use"] }]);
        Assert.Contains("chat.use", unioned[Roles.Guest]);
        Assert.Contains(RolePermissions.Defaults[Roles.Guest][0], unioned[Roles.Guest]);

        var replaced = RoleBaseline.Merge([new ProductRole { Role = Roles.Guest, Permissions = ["chat.use"], Replace = true }]);
        Assert.Equal(["chat.use"], replaced[Roles.Guest]);
    }

    [Fact]
    public void SystemAdmin_IsNotCustomizable()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RoleBaseline.Merge([new ProductRole { Role = Roles.SystemAdmin, Permissions = ["chat.use"] }]));
    }

    [Fact]
    public void ResolutionFallback_UsesTheMergedBaseline()
    {
        var merged = RoleBaseline.Merge([new ProductRole { Role = "paralegal", Permissions = ["chat.use"] }]);
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var permissions = RolePermissionResolution.PermissionsForRoles(["paralegal"], empty, merged);

        Assert.Contains("chat.use", permissions);
    }
}
