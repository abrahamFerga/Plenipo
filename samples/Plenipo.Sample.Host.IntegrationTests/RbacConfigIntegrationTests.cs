using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Auditing;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for the <b>configurable RBAC baseline</b>: a tenant admin can edit what a role
/// grants, the change flows through to every holder's effective permissions on their next request, and the
/// system_admin role and the management gate are protected. The mutation case runs in its own tenant so it
/// can't perturb the shared dev tenant the other tests rely on.
/// </summary>
[Collection("api")]
public sealed class RbacConfigIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Roles_ReportEditableFlags_AndSystemAdminIsFixedAtWildcard()
    {
        var roles = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/admin/roles");

        var byName = roles.EnumerateArray().ToDictionary(r => r.GetProperty("role").GetString()!);

        // system_admin is the lockout guardrail: always "*", never editable.
        var sysAdmin = byName["system_admin"];
        Assert.False(sysAdmin.GetProperty("editable").GetBoolean());
        var sysAdminPerms = sysAdmin.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Single(sysAdminPerms);
        Assert.Equal("*", sysAdminPerms[0]);

        // The others are editable.
        Assert.True(byName["user"].GetProperty("editable").GetBoolean());
        Assert.True(byName["tenant_admin"].GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task EditingARole_ChangesEffectivePermissions_ForItsHolders()
    {
        const string tenant = "rbac-edit";
        await fixture.EnsureTenantAsync(tenant);

        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Baseline: a plain user in this tenant may chat but may NOT manage approvals (the default).
        var before = await fixture.ClientForTenant("user", tenant).GetFromJsonAsync<JsonElement>("/api/platform/me");
        var beforePerms = before.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("chat.use", beforePerms);
        Assert.DoesNotContain("chat.approvals.manage", beforePerms);

        // Configure the `user` role to additionally grant approvals management — no code change, just config.
        var body = new { permissions = new[] { "chat.use", "chat.conversations.view", "chat.approvals.manage" } };
        using var put = await admin.PutAsJsonAsync("/api/admin/roles/user/permissions", body);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // The change is reflected in every `user`'s effective permissions on their next request…
        var after = await fixture.ClientForTenant("user", tenant).GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.Contains("chat.approvals.manage",
            after.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        // …and the permission gate honours it: the approvals endpoint (RequireAuthorization ManageApprovals)
        // now admits a plain user in this tenant, where by default it would be Forbidden.
        using var approvals = await fixture.ClientForTenant("user", tenant).GetAsync("/api/chat/approvals");
        Assert.Equal(HttpStatusCode.OK, approvals.StatusCode);

        // Isolation guard: the dev tenant's `user` is untouched — still no approvals permission.
        var devUser = await fixture.ClientFor("user").GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.DoesNotContain("chat.approvals.manage",
            devUser.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task EditingARole_IsRecordedInTheAuditTrail()
    {
        const string tenant = "rbac-audit";
        await fixture.EnsureTenantAsync(tenant);

        var body = new { permissions = new[] { "chat.use", "chat.conversations.view", "chat.approvals.manage" } };
        using var put = await fixture.ClientForTenant("system_admin", tenant)
            .PutAsJsonAsync("/api/admin/roles/user/permissions", body);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // A change to what a role grants is a security-config change — it must leave an append-only audit
        // record naming the role and the exact diff.
        var events = await fixture.AuthEventsForTenantAsync(tenant, AuthAuditEventType.RolePermissionsChanged);
        Assert.Contains(events, e =>
            e.Detail is not null
            && e.Detail.Contains("role 'user'", StringComparison.Ordinal)
            && e.Detail.Contains("chat.approvals.manage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthEventsEndpoint_SurfacesRoleChanges_AndIsGatedByViewAuditLog()
    {
        const string tenant = "rbac-auth-endpoint";
        await fixture.EnsureTenantAsync(tenant);

        var body = new { permissions = new[] { "chat.use", "chat.approvals.manage" } };
        using var put = await fixture.ClientForTenant("system_admin", tenant)
            .PutAsJsonAsync("/api/admin/roles/user/permissions", body);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        // The admin console's Audit view reads this endpoint — the role change must show up through it.
        var events = await fixture.ClientForTenant("system_admin", tenant)
            .GetFromJsonAsync<JsonElement>("/api/admin/audit/auth-events?take=100");
        Assert.Contains(events.EnumerateArray(), e =>
            e.GetProperty("eventType").GetString() == "RolePermissionsChanged"
            && (e.GetProperty("detail").GetString() ?? "").Contains("role 'user'", StringComparison.Ordinal));

        // Reading the security audit trail requires platform.audit.view — a plain user is refused.
        using var denied = await fixture.ClientFor("user").GetAsync("/api/admin/audit/auth-events");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task UserRoleAndPermissionGrants_AreAudited()
    {
        const string tenant = "rbac-user-audit";
        const string targetEmail = "rbac-target@example.com";
        await fixture.EnsureTenantAsync(tenant);

        // Provision a target user in this tenant with a distinct email so we can find it (the dev-auth
        // default email is shared across users). One authenticated request JIT-provisions them.
        var target = fixture.Factory.CreateClient();
        target.DefaultRequestHeaders.Add("X-Dev-Subject", "rbac-target-user");
        target.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        target.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        target.DefaultRequestHeaders.Add("X-Dev-Email", targetEmail);
        await target.GetFromJsonAsync<JsonElement>("/api/platform/me");

        var admin = fixture.ClientForTenant("system_admin", tenant);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var targetId = users.EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == targetEmail)
            .GetProperty("id").GetString();

        using var grant = await admin.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/permissions", new { permission = "tools.finance.summarize_spending" });
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);

        using var assign = await admin.PostAsJsonAsync(
            $"/api/admin/users/{targetId}/roles", new { role = "tenant_admin" });
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        // Both per-user security changes must appear in the audit trail, naming the target user.
        var events = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/auth-events?take=200");
        var rows = events.EnumerateArray().ToArray();
        Assert.Contains(rows, e => e.GetProperty("eventType").GetString() == "PermissionGranted"
            && (e.GetProperty("detail").GetString() ?? "").Contains(targetEmail, StringComparison.Ordinal));
        Assert.Contains(rows, e => e.GetProperty("eventType").GetString() == "RoleAssigned"
            && (e.GetProperty("detail").GetString() ?? "").Contains(targetEmail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditingSystemAdmin_IsRejected()
    {
        using var response = await fixture.ClientFor("system_admin")
            .PutAsJsonAsync("/api/admin/roles/system_admin/permissions", new { permissions = new[] { "chat.use" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("guest", HttpStatusCode.Forbidden)]
    [InlineData("system_admin", HttpStatusCode.NoContent)]
    public async Task SettingRolePermissions_IsGatedByManageRoles(string role, HttpStatusCode expected)
    {
        // Edit the guest role in the dev tenant to its own default — a real, applied change for the
        // system_admin case that leaves guest's behaviour unchanged, so it can't perturb other tests.
        var body = new { permissions = new[] { "chat.conversations.view" } };
        using var response = await fixture.ClientFor(role)
            .PutAsJsonAsync("/api/admin/roles/guest/permissions", body);

        Assert.Equal(expected, response.StatusCode);
    }
}
