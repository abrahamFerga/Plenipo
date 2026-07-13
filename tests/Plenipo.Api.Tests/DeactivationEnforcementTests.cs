using System.Net;
using System.Net.Http.Json;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the deactivation kill-switch ENFORCEMENT (the admin tests cover the guard against
/// deactivating your own account). Two distinct branches in <c>RequestEnricher</c>, both denying with a 403
/// before any endpoint runs: a deactivated user, and a deactivated tenant (which locks out ALL its users — a
/// wider blast radius). Either retaining access would be a real breach, so both are proven.
/// </summary>
public sealed class DeactivationEnforcementTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public DeactivationEnforcementTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject, string tenant = "dev")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        return client;
    }

    [Fact]
    public async Task A_deactivated_user_is_denied_all_access()
    {
        // Bob signs in — provisioned and active, so he reaches the platform.
        var bob = ClientAs("user", "deact-bob");
        var bobMe = await (await bob.GetAsync("/api/platform/me")).Content.ReadFromJsonAsync<MeDto>();
        Assert.NotNull(bobMe?.userId);

        // An operator (a different user) deactivates Bob.
        var admin = ClientAs("system_admin", "deact-admin");
        (await admin.PutAsJsonAsync($"/api/admin/users/{bobMe!.userId}/active", new { isActive = false }))
            .EnsureSuccessStatusCode();

        // Bob still holds a valid token, but every request is now denied (403) before reaching any endpoint.
        var denied = await bob.GetAsync("/api/platform/me");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Every_user_in_a_deactivated_tenant_is_denied_access()
    {
        var tenantBId = SeedTenant("tenant-b");

        // A user in tenant B can reach the platform while the tenant is active.
        var userB = ClientAs("user", "tenant-b-user", tenant: "tenant-b");
        Assert.Equal(HttpStatusCode.OK, (await userB.GetAsync("/api/platform/me")).StatusCode);

        // An operator (in the dev tenant) deactivates the whole of tenant B.
        var admin = ClientAs("system_admin", "cross-tenant-admin");
        (await admin.PutAsJsonAsync($"/api/admin/tenants/{tenantBId}/active", new { isActive = false }))
            .EnsureSuccessStatusCode();

        // The tenant-wide kill switch now denies its users (a different RequestEnricher branch than the user one).
        Assert.Equal(HttpStatusCode.Forbidden, (await userB.GetAsync("/api/platform/me")).StatusCode);
    }

    private Guid SeedTenant(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = new Tenant { Name = "Tenant B", Slug = slug };
        db.Tenants.Add(tenant);
        db.SaveChanges();
        return tenant.Id;
    }

    private sealed record MeDto(string? userId, string? displayName, string? tenantId, string[] permissions);
}
