using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Operator-level tenant administration: a platform admin can list every tenant and deactivate one as a
/// tenant-wide kill switch (denying all its users), with reactivation restoring access. Guards against
/// deactivating the tenant you're operating in, and the surface is gated by platform.tenants.manage.
/// </summary>
[Collection("api")]
public sealed class TenantManagementIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task DeactivatingATenant_DeniesAllItsUsers_AndReactivationRestoresAccess()
    {
        const string tenant = "tenant-killswitch";
        await fixture.EnsureTenantAsync(tenant);

        // The operator works in the dev tenant; the target is a different tenant.
        var admin = fixture.ClientFor("system_admin");
        var tenants = await admin.GetFromJsonAsync<JsonElement>("/api/admin/tenants");
        var targetId = tenants.EnumerateArray().First(t => t.GetProperty("slug").GetString() == tenant).GetProperty("id").GetString();

        var member = fixture.ClientForTenant("system_admin", tenant);
        using (var ok = await member.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var deactivate = await admin.PutAsJsonAsync($"/api/admin/tenants/{targetId}/active", new { isActive = false }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        }

        // Every request from the deactivated tenant is now denied.
        using (var denied = await member.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        // Reactivation restores access.
        using (var reactivate = await admin.PutAsJsonAsync($"/api/admin/tenants/{targetId}/active", new { isActive = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        }
        using (var restored = await member.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }
    }

    [Fact]
    public async Task CannotDeactivateTheTenantYouAreOperatingIn()
    {
        var admin = fixture.ClientFor("system_admin");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var ownTenantId = me.GetProperty("tenantId").GetString();

        using var response = await admin.PutAsJsonAsync($"/api/admin/tenants/{ownTenantId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("system_admin", HttpStatusCode.OK)]
    public async Task TenantsList_IsGatedByManageTenants(string role, HttpStatusCode expected)
    {
        using var response = await fixture.ClientFor(role).GetAsync("/api/admin/tenants");
        Assert.Equal(expected, response.StatusCode);
    }
}
