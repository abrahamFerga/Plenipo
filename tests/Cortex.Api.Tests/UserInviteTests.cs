using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Cortex.Api.Tests;

/// <summary>
/// Standing email invites: an admin names an address and roles BEFORE the person exists, first
/// sign-in with that email applies them, and a revoked invite applies nothing. Closes the gap
/// where roles could only be granted to users who already had a row.
/// </summary>
public sealed class UserInviteTests : IClassFixture<CortexApiFactory>
{
    private readonly CortexApiFactory _factory;

    public UserInviteTests(CortexApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject, string? email = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add("X-Dev-Email", email);
        }

        return client;
    }

    [Fact]
    public async Task Invited_email_gets_its_roles_at_first_sign_in()
    {
        using var admin = ClientAs("system_admin", "invite-admin");

        // Case-insensitivity is part of the contract: invite mixed-case, sign in lowercase.
        var created = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "Ada.Lovelace@Example.com", roles = new[] { "tenant_admin" } });
        created.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        // No SMTP configured in tests — inviting still works, the admin shares the link.
        Assert.False(body.GetProperty("emailSent").GetBoolean());

        // First sign-in with the invited address (dev-auth carries the email claim).
        using var ada = ClientAs("", "ada-subject", "ada.lovelace@example.com");
        (await ada.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        // The provisioned user carries the invited role, not the default.
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var adaRow = users.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "ada.lovelace@example.com");
        Assert.Contains("tenant_admin", adaRow.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // The invite now reads redeemed in the list.
        var invites = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users/invites");
        var redeemed = invites.EnumerateArray().Single(i => i.GetProperty("email").GetString() == "ada.lovelace@example.com");
        Assert.NotEqual(JsonValueKind.Null, redeemed.GetProperty("redeemedAt").ValueKind);
    }

    [Fact]
    public async Task A_revoked_invite_applies_nothing()
    {
        using var admin = ClientAs("system_admin", "invite-admin-2");

        var created = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "revoked@example.com", roles = new[] { "tenant_admin" } });
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString();

        (await admin.DeleteAsync($"/api/admin/users/invites/{id}")).EnsureSuccessStatusCode();

        using var visitor = ClientAs("", "revoked-subject", "revoked@example.com");
        (await visitor.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var row = users.EnumerateArray().Single(u => u.GetProperty("email").GetString() == "revoked@example.com");
        Assert.DoesNotContain("tenant_admin", row.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Duplicates_and_existing_members_are_rejected_with_a_reason()
    {
        using var admin = ClientAs("system_admin", "invite-admin-3");

        (await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "pending@example.com", roles = Array.Empty<string>() })).EnsureSuccessStatusCode();

        var duplicate = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "pending@example.com", roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // The admin's own account exists already — inviting it is refused.
        var self = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "dev@cortex.local", roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        var garbage = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "not-an-email", roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
    }

    [Fact]
    public async Task Inviting_requires_the_user_management_permission()
    {
        using var user = ClientAs("user", "invite-plain");
        var response = await user.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "sneaky@example.com", roles = new[] { "system_admin" } });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdmin_cannot_invite_a_system_admin()
    {
        using var tenantAdmin = ClientAs("tenant_admin", "invite-tenant-admin");

        var response = await tenantAdmin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "operator-escalation@example.com", roles = new[] { "system_admin" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("operator-reserved", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_invite_role_is_rejected()
    {
        using var admin = ClientAs("system_admin", "invite-role-validator");

        var response = await admin.PostAsJsonAsync("/api/admin/users/invites",
            new { email = "unknown-role@example.com", roles = new[] { "made_up_admin" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown role", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
