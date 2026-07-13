using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// User activation/deactivation: a deactivated user keeps a valid token but is denied every request, and
/// reactivation restores access. Plus the guardrails (no self-deactivation, gated by manage-users) and the
/// admin users list exposing the external subject. Runs the deactivation flow in a dedicated tenant.
/// </summary>
[Collection("api")]
public sealed class UserDeactivationIntegrationTests(IntegrationFixture fixture)
{
    private static HttpClient TargetClient(IntegrationFixture fixture, string tenant, string email)
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "deactivation-target-" + tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Dev-Email", email);
        return client;
    }

    [Fact]
    public async Task DeactivatedUser_IsDeniedEveryRequest_AndReactivationRestoresAccess()
    {
        const string tenant = "user-deactivation";
        const string email = "deactivation-target@example.com";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Provision an active user and confirm they have access.
        var target = TargetClient(fixture, tenant, email);
        using (var ok = await target.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var targetId = users.EnumerateArray().First(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetString();

        // Deactivate them.
        using (var deactivate = await admin.PutAsJsonAsync($"/api/admin/users/{targetId}/active", new { isActive = false }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        }

        // Now even an authenticated-only endpoint is refused — the deactivated account has no access at all.
        using (var denied = await target.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // Reactivate restores access.
        using (var reactivate = await admin.PutAsJsonAsync($"/api/admin/users/{targetId}/active", new { isActive = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        }
        using (var restored = await target.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }
    }

    [Fact]
    public async Task CannotDeactivateYourOwnAccount()
    {
        var admin = fixture.ClientFor("system_admin");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var ownId = me.GetProperty("userId").GetString();

        using var response = await admin.PutAsJsonAsync($"/api/admin/users/{ownId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SettingUserActive_IsGatedByManageUsers()
    {
        using var response = await fixture.ClientFor("user")
            .PutAsJsonAsync($"/api/admin/users/{Guid.NewGuid()}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminUsersList_ExposesTheExternalSubject()
    {
        var users = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/admin/users");
        Assert.All(users.EnumerateArray(), u => Assert.False(string.IsNullOrWhiteSpace(u.GetProperty("subject").GetString())));
    }
}
