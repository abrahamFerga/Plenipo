using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.AspNetCore.Setup;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Issue #72 end to end, against a real Postgres: a change to a product's declared <c>ProductRole</c>
/// baseline must reach a tenant that was provisioned — and then EDITED — before the declaration changed,
/// while a permission that tenant's admin deliberately removed stays removed across the same restart.
///
/// <para>The restart is the point. Before deviation storage, the tenant's first role edit materialized every
/// role's full permission set, and from then on the database was authoritative: a later
/// <c>AddPlenipoRole</c> could not reach the tenant, and a role declared afterwards granted nothing at all.
/// A product shipping a permission fix therefore had no way to deliver it.</para>
/// </summary>
[Collection("api")]
public sealed class RoleBaselineIntegrationTests(IntegrationFixture fixture)
{
    private const string Tenant = "baseline-drift";

    /// <summary>
    /// A restarted host over the same database whose product declares ONE MORE permission on "paralegal" —
    /// exactly what a product shipping a permission fix in a new release looks like.
    /// </summary>
    private sealed class RedeclaredRoleFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
                services.AddPlenipoRole("paralegal", [Permissions.ManageApprovals]));
        }
    }

    private static HttpClient ClientOn(WebApplicationFactory<Program> factory, string subject, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", Tenant);
        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Dev-Roles", string.Join(",", roles));
        }

        return client;
    }

    private static async Task<string[]> PermissionsForRoleAsync(HttpClient admin, string role)
    {
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");
        return roles.EnumerateArray()
            .First(r => r.GetProperty("role").GetString() == role)
            .GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToArray();
    }

    private static async Task<string[]> MyPermissionsAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        return me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray();
    }

    [Fact]
    public async Task A_newly_declared_permission_reaches_a_tenant_that_has_already_edited_its_roles()
    {
        await fixture.EnsureTenantAsync(Tenant);

        // 1. The tenant admin edits a role — under the old storage, the moment that froze the tenant.
        //    The edit deliberately REMOVES a baseline permission from `user`, so half (b) has something to
        //    prove and the tenant is genuinely in the "has deviations" state.
        using var admin = ClientOn(fixture.Factory, "drift-admin", "system_admin");
        var userBefore = await PermissionsForRoleAsync(admin, Roles.User);
        Assert.Contains(Permissions.ViewConversations, userBefore);

        (await admin.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.User}/permissions",
            new { permissions = userBefore.Where(p => p != Permissions.ViewConversations).ToArray() }))
            .EnsureSuccessStatusCode();

        // 2. The product ships a release declaring an extra permission on `paralegal`, and the host restarts.
        using var restarted = new RedeclaredRoleFactory();
        using var adminAfter = ClientOn(restarted, "drift-admin", "system_admin");

        // (a) The new permission is there — no reconciler, no admin action, no per-tenant repair.
        Assert.Contains(Permissions.ManageApprovals, await PermissionsForRoleAsync(adminAfter, "paralegal"));

        // ...and it reaches an actual principal, not just the admin listing.
        using var paralegal = ClientOn(restarted, "drift-paralegal", "paralegal");
        Assert.Contains(Permissions.ManageApprovals, await MyPermissionsAsync(paralegal));

        // (b) The permission the admin removed is STILL removed. This is the half a naive add-only
        //     reconciler breaks, and it must survive the same restart that delivered (a).
        Assert.DoesNotContain(Permissions.ViewConversations, await PermissionsForRoleAsync(adminAfter, Roles.User));

        using var user = ClientOn(restarted, "drift-user", Roles.User);
        Assert.DoesNotContain(Permissions.ViewConversations, await MyPermissionsAsync(user));

        // (c) The rest of `paralegal`'s declared baseline still applies — the new declaration unions with it.
        Assert.Contains("legal.matters.view", await PermissionsForRoleAsync(adminAfter, "paralegal"));
    }

    [Fact]
    public async Task A_role_the_tenant_never_touched_still_tracks_the_declared_baseline()
    {
        const string slug = "baseline-untouched";
        await fixture.EnsureTenantAsync(slug);

        using var admin = fixture.ClientForTenant("system_admin", slug);

        // Editing `user` must not write rows for `guest`. Under the old storage it wrote rows for EVERY
        // role, which is precisely what made a declaration change undeliverable afterwards.
        var userPermissions = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles"))
            .EnumerateArray().First(r => r.GetProperty("role").GetString() == Roles.User)
            .GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray();

        (await admin.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.User}/permissions",
            new { permissions = userPermissions.Where(p => p != Permissions.UploadFiles).ToArray() }))
            .EnsureSuccessStatusCode();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = (await db.Tenants.FirstAsync(t => t.Slug == slug)).Id;

        Assert.False(
            await db.RolePermissions.IgnoreQueryFilters()
                .AnyAsync(r => r.TenantId == tenantId && r.Role == Roles.Guest),
            "editing one role must not materialize rows for another");
    }

    [Fact]
    public async Task The_upgrade_conversion_is_lossless_and_claims_each_tenant_once()
    {
        // The conversion's real path — a transaction plus a conditional UPDATE that claims the tenant and
        // holds a row lock — only exists on a relational provider, so this is the only place it runs. The
        // claim is what stops a second instance reading already-converted rows as if they were legacy and
        // suppressing every role's entire baseline.
        const string slug = "legacy-upgrade";
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        // A tenant exactly as the OLD seed left it: every role's full set materialized, minus one
        // permission its admin removed, plus one it added.
        var baseline = RolePermissions.Defaults;
        var tenant = new Tenant { Name = slug, Slug = slug, RolePermissionsConvertedAt = null };
        db.Tenants.Add(tenant);
        foreach (var (role, permissions) in baseline)
        {
            var rows = role == Roles.User
                ? permissions.Where(p => p != Permissions.UploadFiles).Append("tools.legal.list_deadlines")
                : permissions;
            foreach (var permission in rows)
            {
                db.RolePermissions.Add(new RolePermission { TenantId = tenant.Id, Role = role, Permission = permission });
            }
        }

        await db.SaveChangesAsync();

        var merged = RoleBaseline.Merge(scope.ServiceProvider.GetServices<ProductRole>());
        var converted = await RoleStorageConversion.ConvertAsync(db, merged, NullLogger.Instance);
        Assert.True(converted >= 1);

        // Lossless: `user` keeps exactly what it had — the removal stays removed, the addition stays.
        using var admin = fixture.ClientForTenant("system_admin", slug);
        var after = await PermissionsForRoleAsync(admin, Roles.User);
        Assert.DoesNotContain(Permissions.UploadFiles, after);
        Assert.Contains("tools.legal.list_deadlines", after);
        Assert.Contains(Permissions.UseChat, after);

        // Stamped, and a second pass claims nothing — no re-conversion against its own converted rows.
        Assert.NotNull((await db.Tenants.FirstAsync(t => t.Slug == slug)).RolePermissionsConvertedAt);
        Assert.Equal(0, await RoleStorageConversion.ConvertAsync(db, merged, NullLogger.Instance));
        Assert.Equal(
            after.OrderBy(p => p, StringComparer.Ordinal),
            (await PermissionsForRoleAsync(admin, Roles.User)).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Restoring_suppressions_brings_a_diverged_role_back_to_the_baseline()
    {
        const string slug = "baseline-restore";
        await fixture.EnsureTenantAsync(slug);

        using var admin = fixture.ClientForTenant("system_admin", slug);
        var before = await PermissionsForRoleAsync(admin, Roles.TenantAdmin);

        (await admin.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.TenantAdmin}/permissions",
            new { permissions = before.Where(p => p != Permissions.ViewAuditLog).ToArray() }))
            .EnsureSuccessStatusCode();
        Assert.DoesNotContain(Permissions.ViewAuditLog, await PermissionsForRoleAsync(admin, Roles.TenantAdmin));

        // The one-call repair a product points an operator at when a role has diverged from what it declares.
        (await admin.DeleteAsync($"/api/admin/roles/{Roles.TenantAdmin}/suppressions")).EnsureSuccessStatusCode();

        Assert.Equal(
            before.OrderBy(p => p, StringComparer.Ordinal),
            (await PermissionsForRoleAsync(admin, Roles.TenantAdmin)).OrderBy(p => p, StringComparer.Ordinal));
    }
}
