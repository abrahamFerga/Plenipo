using Plenipo.Application.Authorization;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Issue #70 in the configuration it was reported against: <c>ASPNETCORE_ENVIRONMENT=Production</c> with a
/// configured JWT authority, against real Postgres. Nothing seeds a tenant there, and permissions resolve
/// only after a tenant does — so without a bootstrap path the deployment has no principal who could create
/// one, and every request 403s forever.
///
/// <para>This asserts the database half: after the host starts, the tenant, its admin user and the admin's
/// operator role all exist. The HTTP half cannot be asserted here — no token this repo can mint survives
/// <c>ValidateIssuer</c> against the configured authority — so it is covered in
/// <c>Plenipo.Api.Tests.BootstrapTests</c> under dev auth instead. See docs/TESTING.md.</para>
/// </summary>
[Collection("api")]
public sealed class BootstrapIntegrationTests(IntegrationFixture fixture)
{
    private const string Slug = "bootstrapped-co";
    private const string AdminSubject = "bootstrapped-co-operator";

    /// <summary>
    /// Production + a real authority + the Bootstrap section, over the fixture's Postgres. Auth is
    /// "configured" exactly as in a real deploy; no OIDC metadata is fetched because nothing here
    /// validates a token.
    /// </summary>
    private sealed class BootstrappedProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Auth:Authority", "https://login.example.com/00000000-0000-0000-0000-000000000000/v2.0");
            builder.UseSetting("Auth:Audience", "api://plenipo-bootstrap-tests");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), $"plenipo-dp-{Guid.NewGuid():N}"));
            builder.UseSetting("Bootstrap:TenantSlug", Slug);
            builder.UseSetting("Bootstrap:TenantName", "Bootstrapped Co");
            builder.UseSetting("Bootstrap:AdminEmail", "operator@bootstrapped.example");
            builder.UseSetting("Bootstrap:AdminSubject", AdminSubject);
        }
    }

    [Fact]
    public async Task A_production_host_with_a_configured_authority_bootstraps_its_first_operator()
    {
        using var production = new BootstrappedProductionFactory();
        _ = production.CreateClient(); // starting the host is what runs the bootstrap

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == Slug);
        Assert.NotNull(tenant);
        Assert.Equal("Bootstrapped Co", tenant.Name);

        var admin = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Subject == AdminSubject);
        Assert.NotNull(admin);
        Assert.Equal(tenant.Id, admin.TenantId);

        var roles = await db.UserRoles.IgnoreQueryFilters()
            .Where(r => r.UserId == admin.Id).Select(r => r.Role).ToListAsync();
        Assert.Equal([Roles.SystemAdmin], roles);

        // The whole point: this principal's roles resolve to the permission that was unreachable.
        var resolved = RolePermissionResolution.PermissionsForRoles(
            roles,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            RolePermissions.Defaults);
        Assert.True(PermissionMatcher.IsGranted(resolved, Permissions.ManageTenants));
    }

    [Fact]
    public async Task Restarting_a_bootstrapped_deployment_does_not_create_a_second_operator()
    {
        using (var first = new BootstrappedProductionFactory())
        {
            _ = first.CreateClient();
        }

        using (var second = new BootstrappedProductionFactory())
        {
            _ = second.CreateClient();
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        Assert.Equal(1, await db.Tenants.CountAsync(t => t.Slug == Slug));
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Subject == AdminSubject));
    }
}
