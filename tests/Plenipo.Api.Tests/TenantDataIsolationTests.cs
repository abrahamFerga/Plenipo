using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of multi-tenant isolation at the DATA layer — the global query filter that scopes every
/// tenant-owned entity to the ambient tenant. This is the other half of the isolation story from
/// <see cref="AdminAuthorizationTests"/> (which covers the access layer): even an operator (system_admin, who
/// holds the global wildcard) must not see another tenant's rows, because the filter is keyed on the request's
/// tenant, not on permissions.
/// </summary>
public sealed class TenantDataIsolationTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public TenantDataIsolationTests(PlenipoApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_user_listing_is_scoped_to_the_callers_tenant()
    {
        // Seed a user that belongs to a DIFFERENT tenant, straight into the store.
        var foreignEmail = SeedForeignTenantUser();

        // List users as an operator in the dev tenant.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "iso-op");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.GetAsync("/api/admin/users");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // The query filter must hide the other tenant's user — data isolation holds even for system_admin.
        Assert.DoesNotContain(foreignEmail, body);
        // Sanity: the caller's own tenant is not being wholesale hidden (the filter isolates, not empties).
        Assert.Contains("dev@plenipo.local", body);
    }

    /// <summary>Adds a tenant and one user in it directly, returning that user's (unique) email.</summary>
    private string SeedForeignTenantUser()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = new Tenant { Name = "Foreign Tenant", Slug = $"foreign-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);

        var email = $"outsider-{Guid.NewGuid():N}@foreign.example";
        db.Users.Add(new User
        {
            TenantId = tenant.Id,
            Subject = $"foreign-{Guid.NewGuid():N}",
            Email = email,
            DisplayName = "Outsider",
        });

        db.SaveChanges();
        return email;
    }
}
