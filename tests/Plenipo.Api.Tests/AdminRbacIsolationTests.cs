using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end endpoint tests for the multi-tenant isolation guarantee (task_681a9caf): a tenant admin must not
/// be able to grant, or assign a role conferring, an operator-reserved cross-tenant permission — while the
/// operator (system_admin) still can. Exercises the real HTTP pipeline (dev-auth → permission resolution from
/// the seeded dev-tenant role baseline → the admin endpoints' escalation guards) over an in-memory database.
/// </summary>
public sealed class AdminRbacIsolationTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AdminRbacIsolationTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task Harness_boots_and_dev_auth_resolves_an_identity()
    {
        var client = ClientAs("system_admin", "smoke-user");

        var response = await client.GetAsync("/api/platform/me");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TenantAdmin_cannot_create_a_role_granting_cross_tenant_management()
    {
        var client = ClientAs("tenant_admin", "ta-create");

        var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new { role = "sneaky_role", permissions = new[] { "platform.tenants.manage" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("operator", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TenantAdmin_cannot_create_a_role_with_a_platform_wildcard_covering_tenant_management()
    {
        var client = ClientAs("tenant_admin", "ta-wildcard");

        var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new { role = "wildcard_role", permissions = new[] { "platform.*" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_cannot_assign_the_system_admin_role()
    {
        var client = ClientAs("tenant_admin", "ta-assign");
        // The dev identity provisions itself as a user on first request; grab its id from /me.
        var me = await (await client.GetAsync("/api/platform/me")).Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(me);

        var response = await client.PostAsJsonAsync(
            $"/api/admin/users/{me!.userId}/roles",
            new { role = "system_admin" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Operator_can_create_a_role_granting_cross_tenant_management()
    {
        var client = ClientAs("system_admin", "op-create");

        var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new { role = "delegated_operator", permissions = new[] { "platform.tenants.manage" } });

        // The operator holds the reserved permission, so delegating it is allowed.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private sealed record MeDto(string? userId, string? displayName, string? tenantId, string[] permissions);
}
