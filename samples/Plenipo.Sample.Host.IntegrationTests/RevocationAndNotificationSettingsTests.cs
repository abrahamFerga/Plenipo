using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Closes the last untested admin write paths (endpoint-coverage sweep): revoking a direct
/// permission grant and a role assignment — the "taking access away" half of RBAC, whose failure
/// is a lingering-access security bug — and the notification-settings secret lifecycle (write-only
/// vault contract: null keeps, "" clears, the plaintext never comes back).
/// </summary>
[Collection("api")]
public sealed class RevocationAndNotificationSettingsTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task RevokingPermissionAndRole_RemovesAccess_Audits_AndIsIdempotent()
    {
        const string tenant = "rbac-revoke";
        const string targetEmail = "revoke-target@example.com";
        await fixture.EnsureTenantAsync(tenant);

        // JIT-provision the target user, then find their id by their distinct email.
        var target = fixture.Factory.CreateClient();
        target.DefaultRequestHeaders.Add("X-Dev-Subject", "revoke-target-user");
        target.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        target.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        target.DefaultRequestHeaders.Add("X-Dev-Email", targetEmail);
        await target.GetFromJsonAsync<JsonElement>("/api/platform/me");

        var admin = fixture.ClientForTenant("system_admin", tenant);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var targetId = users.EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == targetEmail)
            .GetProperty("id").GetString();

        // Grant a direct permission — the caller's effective permissions pick it up immediately.
        const string granted = "tools.finance.summarize_spending";
        (await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/permissions", new { permission = granted }))
            .EnsureSuccessStatusCode();
        var me = await target.GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.Contains(granted, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Revoke it: gone from the effective set on the very next request.
        using var revoke = await admin.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/revoke", new { permission = granted });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        me = await target.GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.DoesNotContain(granted, me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // Revoking what is not granted is a quiet no-op, not an error.
        using var again = await admin.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions/revoke", new { permission = granted });
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        // Role assignment round-trip: assign, verify listed, revoke, verify gone.
        (await admin.PostAsJsonAsync($"/api/admin/users/{targetId}/roles", new { role = "tenant_admin" }))
            .EnsureSuccessStatusCode();
        using var revokeRole = await admin.DeleteAsync($"/api/admin/users/{targetId}/roles/tenant_admin");
        Assert.Equal(HttpStatusCode.NoContent, revokeRole.StatusCode);
        users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var roles = users.EnumerateArray()
            .First(u => u.GetProperty("id").GetString() == targetId)
            .GetProperty("roles").EnumerateArray().Select(r => r.GetString());
        Assert.DoesNotContain("tenant_admin", roles);

        // Both removals land in the audit trail naming the target.
        var events = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/auth-events?take=200");
        var rows = events.EnumerateArray().ToArray();
        Assert.Contains(rows, e => e.GetProperty("eventType").GetString() == "PermissionRevoked"
            && (e.GetProperty("detail").GetString() ?? "").Contains(targetEmail, StringComparison.Ordinal));
        Assert.Contains(rows, e => e.GetProperty("eventType").GetString() == "RoleRevoked"
            && (e.GetProperty("detail").GetString() ?? "").Contains(targetEmail, StringComparison.Ordinal));

        // Revocation endpoints are themselves permission-gated.
        using var denied = await fixture.ClientForTenant("user", tenant)
            .PostAsJsonAsync($"/api/admin/users/{targetId}/permissions/revoke", new { permission = granted });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task NotificationSettings_SecretIsWriteOnly_NullKeeps_EmptyClears()
    {
        const string tenant = "notif-settings";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Store URL + secret; the read reports the URL and THAT a secret exists — never the value.
        (await admin.PutAsJsonAsync("/api/admin/notification-settings",
            new { webhookUrl = "https://hooks.example/plenipo", webhookSecret = "super-s3cret" }))
            .EnsureSuccessStatusCode();
        using var read = await admin.GetAsync("/api/admin/notification-settings");
        read.EnsureSuccessStatusCode();
        var raw = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-s3cret", raw);
        var settings = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal("https://hooks.example/plenipo", settings.GetProperty("webhookUrl").GetString());
        Assert.True(settings.GetProperty("hasWebhookSecret").GetBoolean());

        // Omitted/null secret keeps what is stored (the UI never has it to echo back)…
        (await admin.PutAsJsonAsync("/api/admin/notification-settings",
            new { webhookUrl = "https://hooks.example/plenipo", webhookSecret = (string?)null }))
            .EnsureSuccessStatusCode();
        var kept = await admin.GetFromJsonAsync<JsonElement>("/api/admin/notification-settings");
        Assert.True(kept.GetProperty("hasWebhookSecret").GetBoolean());

        // …and an empty string explicitly clears it.
        (await admin.PutAsJsonAsync("/api/admin/notification-settings",
            new { webhookUrl = "https://hooks.example/plenipo", webhookSecret = "" }))
            .EnsureSuccessStatusCode();
        var cleared = await admin.GetFromJsonAsync<JsonElement>("/api/admin/notification-settings");
        Assert.False(cleared.GetProperty("hasWebhookSecret").GetBoolean());

        // A relative URL is rejected before anything is stored.
        using var bad = await admin.PutAsJsonAsync("/api/admin/notification-settings",
            new { webhookUrl = "not-a-url", webhookSecret = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Both verbs are gated on platform.notifications.manage.
        using var deniedRead = await fixture.ClientForTenant("user", tenant).GetAsync("/api/admin/notification-settings");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        using var deniedWrite = await fixture.ClientForTenant("user", tenant)
            .PutAsJsonAsync("/api/admin/notification-settings", new { webhookUrl = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);

        // Leave the tenant with delivery disabled.
        (await admin.PutAsJsonAsync("/api/admin/notification-settings",
            new { webhookUrl = (string?)null, webhookSecret = "" })).EnsureSuccessStatusCode();
    }
}
