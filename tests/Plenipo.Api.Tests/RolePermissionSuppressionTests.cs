using System.Net;
using System.Net.Http.Json;
using Plenipo.Application.Authorization;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The three properties issue #72 asks for, exercised over the real admin HTTP surface:
/// (a) a permission a product declares reaches a tenant that has already edited roles,
/// (b) a permission a tenant admin removed stays removed, and
/// (c) editing one role never freezes another against the declared baseline.
/// </summary>
public sealed class RolePermissionSuppressionTests : IDisposable
{
    // Every test here MUTATES the tenant's role configuration, so they cannot share one. xUnit builds a new
    // instance of the class per test method, so a factory owned by the instance gives each test a private
    // in-memory database — without that, one test's suppression is another test's starting state.
    private readonly PlenipoApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private sealed record RoleDto(string Role, string[] Permissions, bool Editable, bool BuiltIn);

    private static async Task<string[]> PermissionsForAsync(HttpClient client, string role)
    {
        var roles = await client.GetFromJsonAsync<RoleDto[]>("/api/admin/roles");
        return roles!.Single(r => r.Role == role).Permissions;
    }

    [Fact]
    public async Task Putting_a_role_without_a_baseline_permission_writes_a_suppression_not_a_deletion()
    {
        var client = ClientAs("system_admin", "supp-write");

        var before = await PermissionsForAsync(client, Roles.User);
        var desired = before.Where(p => p != Permissions.ViewConversations).ToArray();
        Assert.Contains(Permissions.ViewConversations, before); // guard: the fixture really grants it

        var response = await client.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.User}/permissions", new { permissions = desired });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The wire contract round-trips exactly what was submitted...
        Assert.Equal(
            desired.OrderBy(p => p, StringComparer.Ordinal),
            (await PermissionsForAsync(client, Roles.User)).OrderBy(p => p, StringComparer.Ordinal));

        // ...but what is STORED is the deviation: an explicit suppression, which is the state that lets a
        // later baseline change land without resurrecting this removal.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.True(await db.RolePermissionSuppressions.IgnoreQueryFilters()
            .AnyAsync(r => r.Role == Roles.User && r.Permission == Permissions.ViewConversations));
    }

    [Fact]
    public async Task Editing_one_role_leaves_other_roles_tracking_the_baseline()
    {
        // The core regression. Under the old storage the first PUT materialized EVERY role's full set, so
        // `guest` was frozen from that moment on and a later AddPlenipoRole change could never reach it.
        var client = ClientAs("system_admin", "supp-isolate");

        var guestBefore = await PermissionsForAsync(client, Roles.Guest);

        var userPermissions = await PermissionsForAsync(client, Roles.User);
        var response = await client.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.User}/permissions",
            new { permissions = userPermissions.Where(p => p != Permissions.ViewConversations).ToArray() });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(guestBefore, await PermissionsForAsync(client, Roles.Guest));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(
            await db.RolePermissions.IgnoreQueryFilters().AnyAsync(r => r.Role == Roles.Guest),
            "editing `user` must not write rows for `guest` — that is what froze it against the baseline");
    }

    [Fact]
    public async Task Adding_a_permission_beyond_the_baseline_is_stored_as_a_grant()
    {
        var client = ClientAs("system_admin", "supp-grant");

        var desired = (await PermissionsForAsync(client, Roles.Guest)).Append(Permissions.UseChat).ToArray();

        var response = await client.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.Guest}/permissions", new { permissions = desired });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Contains(Permissions.UseChat, await PermissionsForAsync(client, Roles.Guest));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.RolePermissions.IgnoreQueryFilters()
            .Where(r => r.Role == Roles.Guest).Select(r => r.Permission).ToListAsync();
        Assert.Equal([Permissions.UseChat], stored); // only the DIFFERENCE is persisted
    }

    [Fact]
    public async Task Deleting_suppressions_restores_the_declared_baseline()
    {
        var client = ClientAs("system_admin", "supp-reset");

        var before = await PermissionsForAsync(client, Roles.TenantAdmin);
        await client.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.TenantAdmin}/permissions",
            new { permissions = before.Where(p => p != Permissions.ViewAuditLog).ToArray() });
        Assert.DoesNotContain(Permissions.ViewAuditLog, await PermissionsForAsync(client, Roles.TenantAdmin));

        var response = await client.DeleteAsync($"/api/admin/roles/{Roles.TenantAdmin}/suppressions");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(
            before.OrderBy(p => p, StringComparer.Ordinal),
            (await PermissionsForAsync(client, Roles.TenantAdmin)).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Deleting_suppressions_when_there_are_none_is_a_not_found()
    {
        var client = ClientAs("system_admin", "supp-none");

        var response = await client.DeleteAsync($"/api/admin/roles/{Roles.Guest}/suppressions");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resetting_system_admin_suppressions_is_rejected()
    {
        var client = ClientAs("system_admin", "supp-sysadmin");

        var response = await client.DeleteAsync($"/api/admin/roles/{Roles.SystemAdmin}/suppressions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_role_manager_without_the_permission_cannot_reset_suppressions()
    {
        var client = ClientAs(Roles.User, "supp-forbidden");

        var response = await client.DeleteAsync($"/api/admin/roles/{Roles.Guest}/suppressions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_declared_product_role_cannot_be_deleted()
    {
        // A declared role is code, not data. Deleting its rows would not remove it — it would RESET it,
        // dropping the tenant's suppressions so every holder gets back permissions the admin removed,
        // while also purging their assignments. The role reappears at full baseline on the next GET.
        var client = ClientAs("system_admin", "supp-delete-declared");

        var response = await client.DeleteAsync($"/api/admin/roles/{TestModule.ProductRoleName}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("declared", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resetting_suppressions_cannot_restore_an_operator_reserved_permission()
    {
        // The escalation this closes: suppress an operator permission out of a declared role, assign
        // yourself the now-harmless role, then undo the suppression here. The reset is a GRANT path and
        // must clear the same bar as PUT.
        var client = ClientAs("system_admin", "supp-escalate-setup");
        var role = TestModule.OperatorGradeRoleName;

        // A system_admin may narrow it (they hold the permission being suppressed).
        var before = await PermissionsForAsync(client, role);
        Assert.Contains(Permissions.ManageTenants, before);
        (await client.PutAsJsonAsync(
            $"/api/admin/roles/{role}/permissions",
            new { permissions = before.Where(p => p != Permissions.ManageTenants).ToArray() }))
            .EnsureSuccessStatusCode();

        // ...but a tenant admin, who does not, may not undo that suppression.
        var tenantAdmin = ClientAs("tenant_admin", "supp-escalate-attacker");
        var response = await tenantAdmin.DeleteAsync($"/api/admin/roles/{role}/suppressions");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("operator", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Permissions.ManageTenants, await PermissionsForAsync(client, role));
    }

    [Fact]
    public async Task A_tenant_admin_still_cannot_grant_an_operator_reserved_permission()
    {
        // Invariant guard for the new write path: the escalation check must run before the diff.
        var client = ClientAs("tenant_admin", "supp-escalate");

        var response = await client.PutAsJsonAsync(
            $"/api/admin/roles/{Roles.User}/permissions",
            new { permissions = new[] { Permissions.UseChat, "platform.tenants.manage" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("operator", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
