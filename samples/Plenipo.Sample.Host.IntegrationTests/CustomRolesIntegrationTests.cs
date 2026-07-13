using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Custom (tenant-defined) roles: a tenant admin can create a role, assign it, have it grant real access,
/// and delete it — all without a code change. Runs in dedicated tenants so it can't perturb the shared dev
/// tenant the other tests rely on.
/// </summary>
[Collection("api")]
public sealed class CustomRolesIntegrationTests(IntegrationFixture fixture)
{
    private static HttpClient TargetClient(IntegrationFixture fixture, string tenant, string email)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "custom-role-target-" + tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Dev-Email", email);
        return client;
    }

    [Fact]
    public async Task CustomRole_Lifecycle_GrantsRealAccess_ThenRevokesItOnDelete()
    {
        const string tenant = "custom-roles";
        const string email = "auditor-target@example.com";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Provision a plain user who, by default, cannot read the audit log.
        var target = TargetClient(fixture, tenant, email);
        await target.GetFromJsonAsync<JsonElement>("/api/platform/me");
        using (var deniedBefore = await target.GetAsync("/api/admin/audit/tool-calls"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedBefore.StatusCode);
        }

        // Create a custom "auditor" role that grants exactly the audit-view permission.
        using (var create = await admin.PostAsJsonAsync("/api/admin/roles",
            new { role = "auditor", permissions = new[] { "platform.audit.view" } }))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        // It shows up in the role list as a custom (non-built-in) role.
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");
        var auditor = roles.EnumerateArray().First(r => r.GetProperty("role").GetString() == "auditor");
        Assert.False(auditor.GetProperty("builtIn").GetBoolean());
        Assert.True(auditor.GetProperty("editable").GetBoolean());
        Assert.Contains("platform.audit.view",
            auditor.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Assign the custom role to the target user.
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var targetId = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetString();
        using (var assign = await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/roles", new { role = "auditor" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        }

        // The custom role now actually grants access: the target can read the audit log where before it was denied.
        using (var allowed = await target.GetAsync("/api/admin/audit/tool-calls"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // Delete the role — its permission rows and assignments go with it…
        using (var delete = await admin.DeleteAsync("/api/admin/roles/auditor"))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }
        var rolesAfter = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");
        Assert.DoesNotContain(rolesAfter.EnumerateArray(), r => r.GetProperty("role").GetString() == "auditor");

        // …so the target loses the access the role granted.
        using (var deniedAfter = await target.GetAsync("/api/admin/audit/tool-calls"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedAfter.StatusCode);
        }
    }

    [Theory]
    [InlineData("user")]            // built-in name
    [InlineData("system_admin")]    // built-in name
    [InlineData("Bad Name")]        // invalid characters
    [InlineData("x")]               // too short
    public async Task CreateRole_RejectsInvalidNames(string name)
    {
        using var response = await fixture.ClientFor("system_admin")
            .PostAsJsonAsync("/api/admin/roles", new { role = name, permissions = new[] { "chat.use" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRole_WithNoPermissions_IsRejected()
    {
        using var response = await fixture.ClientFor("system_admin")
            .PostAsJsonAsync("/api/admin/roles", new { role = "empty_role", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeletingABuiltInRole_IsRejected()
    {
        using var response = await fixture.ClientFor("system_admin").DeleteAsync("/api/admin/roles/user");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateRole_IsGatedByManageRoles()
    {
        using var response = await fixture.ClientFor("user")
            .PostAsJsonAsync("/api/admin/roles", new { role = "sneaky", permissions = new[] { "*" } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
