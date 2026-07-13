using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the RBAC authorization gate — the <c>RequireAuthorization(PermissionRequirement…)</c>
/// that every admin endpoint sits behind. This is the most fundamental security mechanism (if it fails open,
/// every downstream validation is moot), yet it had no endpoint test. Also pins the isolation guarantee at the
/// ACCESS layer: a tenant admin can't even reach the cross-tenant <c>/tenants</c> surface, complementing the
/// grant-level checks in <see cref="AdminRbacIsolationTests"/>.
/// </summary>
public sealed class AdminAuthorizationTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AdminAuthorizationTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_plain_user_is_forbidden_from_the_admin_surface()
    {
        var response = await ClientAs("user", "authz-user").GetAsync("/api/admin/roles");

        // Authenticated (dev-auth) but lacking platform.roles.manage → 403, not 401.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_can_manage_roles_but_is_forbidden_from_the_cross_tenant_surface()
    {
        var client = ClientAs("tenant_admin", "authz-ta");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/roles")).StatusCode);
        // The isolation guarantee at the access layer: a tenant admin cannot even LIST other tenants.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task Operator_can_access_both_the_role_and_tenant_surfaces()
    {
        var client = ClientAs("system_admin", "authz-op");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/tenants")).StatusCode);
    }

    [Fact]
    public async Task A_user_cannot_deactivate_their_own_account()
    {
        var client = ClientAs("system_admin", "authz-self");
        var me = await (await client.GetAsync("/api/platform/me")).Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(me);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/users/{me!.userId}/active",
            new { isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record MeDto(string? userId, string? displayName, string? tenantId, string[] permissions);
}
