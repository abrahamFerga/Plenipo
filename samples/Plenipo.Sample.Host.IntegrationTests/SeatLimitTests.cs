using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Subscription seat enforcement (commercialization phase 3): a tenant provisioned with
/// MaxSeats admits no NEW users once its active-user count reaches the limit — existing users
/// keep signing in, and deactivating a user frees a seat. Refusals land in the auth audit.
/// </summary>
[Collection("api")]
public sealed class SeatLimitTests(IntegrationFixture fixture)
{
    private static HttpClient UserClient(IntegrationFixture f, string subject, string tenant)
    {
        var client = f.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        return client;
    }

    [Fact]
    public async Task FullTenant_RefusesNewUsers_UntilASeatFrees()
    {
        // A two-seat tenant; the provisioned admin occupies seat 1.
        using var operator_ = fixture.ClientFor("system_admin");
        (await operator_.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Two Seats Co",
            slug = "two-seats",
            adminEmail = "admin@twoseats.example",
            adminSubject = "twoseats-admin",
            maxSeats = 2,
        })).EnsureSuccessStatusCode();

        // Seat 2: the first employee JIT-provisions fine.
        using var second = UserClient(fixture, "twoseats-emp-1", "two-seats");
        (await second.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        // Seat 3: refused — the tenant is full. (Enrichment denies, so the request never runs.)
        using var third = UserClient(fixture, "twoseats-emp-2", "two-seats");
        using var refused = await third.GetAsync("/api/platform/me");
        Assert.True(refused.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"expected an auth denial, got {(int)refused.StatusCode}");

        // Existing users are untouched by a full tenant.
        (await second.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        // The refusal is auditable.
        using var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "twoseats-admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "two-seats");
        var events = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/auth-events?take=100");
        Assert.Contains(events.EnumerateArray(), e =>
            e.GetProperty("eventType").GetString() == "SeatLimitDenied" &&
            (e.GetProperty("subject").GetString() ?? "") == "twoseats-emp-2");

        // Deactivate the employee → a seat frees → the refused user now gets in.
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var empId = users.EnumerateArray()
            .First(u => u.GetProperty("subject").GetString() == "twoseats-emp-1")
            .GetProperty("id").GetString();
        (await admin.PutAsJsonAsync($"/api/admin/users/{empId}/active", new { isActive = false }))
            .EnsureSuccessStatusCode();

        using var thirdRetry = await third.GetAsync("/api/platform/me");
        Assert.Equal(HttpStatusCode.OK, thirdRetry.StatusCode);
    }
}
