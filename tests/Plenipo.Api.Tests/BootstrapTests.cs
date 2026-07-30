using System.Net;
using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.Application.Bootstrap;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Issue #70: a deployment with no tenant has nobody who could create one. Permissions are only resolved
/// after a tenant resolves, so every principal — including one asserting system_admin — carries an empty
/// permission set and is refused by <c>POST /api/admin/tenants</c>, the very endpoint that would fix it.
///
/// <para>The Bootstrap section breaks that deadlock from configuration, once, with no HTTP surface and no
/// standing bypass. These tests prove it produces a principal who can actually administer the deployment,
/// and that it disarms itself the moment one exists.</para>
/// </summary>
public sealed class BootstrapTests : IDisposable
{
    // Each test boots its own host with its own Bootstrap configuration, so none can see another's tenant.
    private readonly List<PlenipoApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private sealed class BootstrappedFactory(IReadOnlyDictionary<string, string> settings) : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private PlenipoApiFactory Factory(params (string Key, string Value)[] settings)
    {
        var factory = new BootstrappedFactory(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
        _factories.Add(factory);
        return factory;
    }

    /// <summary>
    /// A client whose token asserts NO roles. The dev handler defaults an ABSENT X-Dev-Roles header to
    /// system_admin, which would mask every result here — a present-but-separator-only value is a
    /// deliberately role-less token, so DB assignments are the only thing that can grant anything.
    /// </summary>
    private static HttpClient RolelessClient(PlenipoApiFactory factory, string subject, string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", ",");
        return client;
    }

    [Fact]
    public async Task Bootstrap_admits_an_operator_who_can_administer_the_deployment()
    {
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:TenantName", "Acme Ltd"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminSubject", "acme-admin"));

        using var client = RolelessClient(factory, "acme-admin", "acme");

        // The endpoint that was unreachable: it needs platform.tenants.manage, which needs permissions,
        // which need a tenant. Bootstrap supplied all three.
        var response = await client.GetAsync("/api/admin/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_creates_the_tenant_and_the_admin_with_the_declared_roles()
    {
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminSubject", "acme-admin"));
        _ = factory.CreateClient(); // force startup

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == "acme");
        Assert.NotNull(tenant);

        var admin = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Subject == "acme-admin");
        Assert.NotNull(admin);
        Assert.Equal(tenant.Id, admin.TenantId);

        var roles = await db.UserRoles.IgnoreQueryFilters()
            .Where(r => r.UserId == admin.Id).Select(r => r.Role).ToListAsync();
        Assert.Equal([Roles.SystemAdmin], roles);
    }

    [Fact]
    public async Task Bootstrap_records_an_audit_event()
    {
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminSubject", "acme-admin"));
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var events = await audit.AuthEvents
            .Where(e => e.EventType == AuthAuditEventType.PlatformBootstrapped)
            .ToListAsync();

        var bootstrapped = Assert.Single(events);
        Assert.NotNull(bootstrapped.TenantId);
        Assert.Contains("acme", bootstrapped.Detail, StringComparison.Ordinal);
        Assert.Contains(Roles.SystemAdmin, bootstrapped.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_is_inert_once_the_deployment_has_an_operator()
    {
        // The section must not be a standing door: running it twice must not mint a second operator.
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminSubject", "acme-admin"));
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var again = await scope.ServiceProvider.GetRequiredService<IPlatformBootstrapper>().BootstrapAsync();

        Assert.Equal(BootstrapOutcome.AlreadyBootstrapped, again.Outcome);

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Subject == "acme-admin"));
    }

    [Fact]
    public async Task An_unconfigured_deployment_bootstraps_nothing()
    {
        var factory = Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IPlatformBootstrapper>().BootstrapAsync();

        Assert.Equal(BootstrapOutcome.NotConfigured, result.Outcome);
    }

    [Fact]
    public async Task A_tenant_with_no_operator_does_not_disarm_bootstrap()
    {
        // The gate is deliberately NOT "does a tenant exist". A tenant provisioned through commerce gets a
        // tenant_admin, which lacks platform.tenants.manage by design — so a tenant-existence gate would
        // disarm bootstrap permanently while leaving nobody able to create a tenant. Permanent lockout.
        var factory = Factory();
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = new Tenant { Name = "Paying Customer", Slug = "paying" };
        db.Tenants.Add(tenant);
        var user = new User { TenantId = tenant.Id, Subject = "paying-admin", Email = "admin@paying.test" };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = Roles.TenantAdmin });
        await db.SaveChangesAsync();

        var bootstrapper = scope.ServiceProvider.GetRequiredService<IPlatformBootstrapper>();
        Assert.Equal(BootstrapOutcome.NotConfigured, (await bootstrapper.BootstrapAsync()).Outcome);

        // ...and with a section configured, it still fires — the deployment has no operator.
        var configured = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminSubject", "acme-admin"));
        using var client = RolelessClient(configured, "acme-admin", "acme");
        Assert.Equal(HttpStatusCode.OK, await client.GetAsync("/api/admin/tenants").ContinueWith(t => t.Result.StatusCode));
    }

    [Fact]
    public async Task An_email_only_bootstrap_binds_its_roles_through_a_standing_invite()
    {
        // Without a subject the roles cannot be attached to a user row that will match the IdP's `sub`
        // (TenantProvisioningService defaults a missing subject to the EMAIL). A standing invite is the
        // one shape that survives a real IdP: RequestEnricher redeems it at first sign-in.
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminRoles:0", Roles.TenantAdmin));
        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var invite = await db.UserInvites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Email == "admin@acme.test");
        Assert.NotNull(invite);
        Assert.Equal([Roles.TenantAdmin], invite.RoleList());
        Assert.Null(invite.RedeemedAt);
    }

    [Fact]
    public async Task An_email_only_bootstrap_grants_its_roles_at_first_sign_in()
    {
        var factory = Factory(
            ("Bootstrap:TenantSlug", "acme"),
            ("Bootstrap:AdminEmail", "admin@acme.test"),
            ("Bootstrap:AdminRoles:0", Roles.TenantAdmin));

        // A previously unseen subject presenting the invited address — what a real IdP would send.
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "idp-minted-sub-12345");
        client.DefaultRequestHeaders.Add("X-Dev-Email", "admin@acme.test");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "acme");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", ",");
        using var _ = client;

        // tenant_admin holds platform.users.manage but NOT platform.tenants.manage (that is operator-only).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/tenants")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var invite = await db.UserInvites.IgnoreQueryFilters().FirstAsync(i => i.Email == "admin@acme.test");
        Assert.NotNull(invite.RedeemedAt);
    }
}
