using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The external-IdP authorization mode (Auth:PermissionSource=Token — e.g. Entra External ID / B2C
/// as the single source of truth): token roles still expand through the tenant's role → permission
/// baselines, but internal DB role assignments and per-user grants are IGNORED, JIT provisioning
/// never invents a default role, and the admin endpoints that would edit internal assignments
/// answer 409 instead of writing rows the resolver would silently discard.
/// </summary>
public sealed class TokenAuthorizationModeTests : IAsyncLifetime
{
    private TokenModeFactory _factory = default!;

    public async Task InitializeAsync()
    {
        _factory = new TokenModeFactory();
        using var warmup = _factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed class TokenModeFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:PermissionSource", "Token");
            base.ConfigureWebHost(builder);
        }
    }

    private HttpClient ClientFor(string roles, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Token_roles_grant_baselines_but_db_assignments_and_grants_are_ignored()
    {
        using var client = ClientFor("user", "idp-user");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();

        // The token's "user" role expands through the baseline as usual.
        Assert.Contains("chat.use", permissions);

        // Sneak in what WOULD escalate in Database mode: a DB role and a direct grant.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "idp-user");
            db.Set<UserRole>().Add(new UserRole { TenantId = user.TenantId, UserId = user.Id, Role = Roles.TenantAdmin });
            db.Set<UserPermission>().Add(new UserPermission { TenantId = user.TenantId, UserId = user.Id, Permission = "tools.test.echo" });
            await db.SaveChangesAsync();
        }

        // The IdP's token is the source of truth: neither the DB role nor the grant is effective.
        me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.DoesNotContain("platform.users.manage", permissions); // tenant_admin baseline NOT applied
        Assert.DoesNotContain("tools.test.echo", permissions);       // direct grant NOT applied
        Assert.Contains("chat.use", permissions);                     // token role still is
    }

    [Fact]
    public async Task Jit_provisioning_never_invents_a_default_role_in_token_mode()
    {
        // "," parses to zero roles through the dev handler — a token asserting nothing.
        using var client = ClientFor(",", "idp-roleless");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.Empty(me.GetProperty("permissions").EnumerateArray());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "idp-roleless");
        var dbRoles = await db.Set<UserRole>().IgnoreQueryFilters().Where(r => r.UserId == user.Id).ToListAsync();
        Assert.Empty(dbRoles); // no invented "user" role — the IdP decides, nobody else
    }

    [Fact]
    public async Task Internal_rbac_mutation_endpoints_answer_409_with_guidance()
    {
        using var admin = ClientFor("system_admin", "idp-admin");
        (await admin.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            userId = (await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "idp-admin")).Id;
        }

        var assign = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/roles", new { role = "user" });
        Assert.Equal(HttpStatusCode.Conflict, assign.StatusCode);
        Assert.Contains("external identity provider", await assign.Content.ReadAsStringAsync());

        var grant = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/permissions", new { permission = "chat.use" });
        Assert.Equal(HttpStatusCode.Conflict, grant.StatusCode);

        var revokeRole = await admin.DeleteAsync($"/api/admin/users/{userId}/roles/user");
        Assert.Equal(HttpStatusCode.Conflict, revokeRole.StatusCode);

        var revokeGrant = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/permissions/revoke", new { permission = "chat.use" });
        Assert.Equal(HttpStatusCode.Conflict, revokeGrant.StatusCode);

        // Role BASELINE editing stays available — it maps IdP role names to Plenipo permissions.
        var baselines = await admin.GetAsync("/api/admin/roles");
        Assert.Equal(HttpStatusCode.OK, baselines.StatusCode);
    }
}
