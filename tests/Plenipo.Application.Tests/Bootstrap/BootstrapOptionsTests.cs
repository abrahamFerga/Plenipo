using Plenipo.Application.Authorization;
using Plenipo.Application.Bootstrap;
using Xunit;

namespace Plenipo.Application.Tests.Bootstrap;

/// <summary>
/// The Bootstrap section decides whether a deployment is usable at all, and it is consumed once at
/// startup — so a typo must fail the host, not produce a deployment that silently never bootstraps and
/// then 403s everything. That silent-403 state is exactly what issue #70 reported.
/// </summary>
public sealed class BootstrapOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string[]> Declared = RoleBaseline.Merge(
        [new ProductRole { Role = "paralegal", Permissions = ["chat.use"] }]);

    private static BootstrapOptions Valid() => new()
    {
        TenantSlug = "acme",
        AdminEmail = "admin@acme.test",
        AdminSubject = "acme-admin",
    };

    [Fact]
    public void A_complete_section_is_valid()
    {
        Valid().ThrowIfInvalid(Declared);
    }

    [Fact]
    public void An_absent_section_is_valid_and_not_configured()
    {
        var options = new BootstrapOptions();

        options.ThrowIfInvalid(Declared);

        Assert.False(options.IsConfigured);
    }

    [Theory]
    [InlineData("acme", null)]   // slug without email
    [InlineData(null, "a@b.test")] // email without slug
    public void A_half_filled_section_fails_fast(string? slug, string? email)
    {
        var options = new BootstrapOptions { TenantSlug = slug, AdminEmail = email };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("half-configured", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Not_A_Slug")]
    [InlineData("-leading-hyphen")]
    [InlineData("")]
    public void An_invalid_slug_names_the_key(string slug)
    {
        var options = Valid();
        options.TenantSlug = slug;

        // An empty slug reads as "not configured" for the slug half, so it trips the half-filled guard;
        // either way the operator is told which key is wrong.
        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-an-email")]
    public void An_invalid_email_names_the_key(string email)
    {
        var options = Valid();
        options.AdminEmail = email;

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:AdminEmail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_email_is_rejected_before_it_reaches_the_column()
    {
        var options = Valid();
        options.AdminEmail = new string('a', 315) + "@b.test"; // 322 chars, column is 320

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:AdminEmail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_subject_is_rejected_before_it_reaches_the_column()
    {
        var options = Valid();
        options.AdminSubject = new string('s', 201); // column is 200

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:AdminSubject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_role_is_rejected()
    {
        var options = Valid();
        options.AdminRoles = ["not_a_role"];

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("unknown role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_declared_product_role_is_accepted()
    {
        var options = Valid();
        options.AdminRoles = ["paralegal"];

        options.ThrowIfInvalid(Declared);
    }

    [Fact]
    public void An_empty_role_entry_is_rejected()
    {
        var options = Valid();
        options.AdminRoles = [Roles.TenantAdmin, "  "];

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:AdminRoles", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuring_roles_REPLACES_the_default_rather_than_adding_to_it()
    {
        // .NET binds a configured array over the property's existing value index by index. If the default
        // were stored in the property, an operator setting Bootstrap__AdminRoles__0=tenant_admin could keep
        // system_admin — silently handing cross-tenant control to someone who asked for a tenant admin.
        // The default therefore lives at the point of use, and this pins that.
        var options = new BootstrapOptions { AdminRoles = [Roles.TenantAdmin] };

        Assert.Equal([Roles.TenantAdmin], options.EffectiveAdminRoles);
        Assert.DoesNotContain(Roles.SystemAdmin, options.EffectiveAdminRoles);
    }

    [Fact]
    public void Operator_roles_require_an_explicit_subject()
    {
        // An email-keyed bootstrap binds roles through a standing invite, which is matched against the
        // token's EMAIL claim — and email is not a verified identifier. Handing cross-tenant control to
        // whoever presents the address first is not acceptable, so this combination is refused outright.
        var options = new BootstrapOptions
        {
            TenantSlug = "acme",
            AdminEmail = "admin@acme.test",
            AdminRoles = [Roles.SystemAdmin],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(Declared));
        Assert.Contains("Bootstrap:AdminSubject", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unverified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tenant_grade_roles_do_not_require_a_subject()
    {
        var options = new BootstrapOptions
        {
            TenantSlug = "acme",
            AdminEmail = "admin@acme.test",
            AdminRoles = [Roles.TenantAdmin],
        };

        options.ThrowIfInvalid(Declared);
    }

    [Fact]
    public void A_product_role_granting_an_operator_permission_also_requires_a_subject()
    {
        // The guard must judge on what a role GRANTS, not on its name — a host can declare a role that
        // holds an operator-reserved permission without calling it system_admin.
        var declared = RoleBaseline.Merge(
            [new ProductRole { Role = "fleet_ops", Permissions = [Permissions.ManageTenants] }]);
        var options = new BootstrapOptions
        {
            TenantSlug = "acme",
            AdminEmail = "admin@acme.test",
            AdminRoles = ["fleet_ops"],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.ThrowIfInvalid(declared));
        Assert.Contains("Bootstrap:AdminSubject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_admin_role_is_system_admin()
    {
        // An operator bootstrapping an empty deployment needs an operator; anything less means they
        // bootstrap and are still locked out.
        Assert.Equal([Roles.SystemAdmin], new BootstrapOptions().EffectiveAdminRoles);
    }
}
