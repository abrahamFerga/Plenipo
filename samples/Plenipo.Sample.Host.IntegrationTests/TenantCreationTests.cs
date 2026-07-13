using System.Net;
using System.Net.Http.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>Tenant creation from the admin surface: happy path, slug rules, uniqueness, gating.</summary>
[Collection("api")]
public sealed class TenantCreationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task CreateTenant_CreatesAndListsIt()
    {
        using var operatorClient = fixture.ClientFor("system_admin");
        var slug = $"acme-{Guid.NewGuid():N}"[..20];

        using var created = await operatorClient.PostAsJsonAsync("/api/admin/tenants",
            new { name = "Acme Legal LLP", slug });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var tenants = await operatorClient.GetFromJsonAsync<List<TenantDto>>("/api/admin/tenants");
        var tenant = Assert.Single(tenants!, t => t.Slug == slug);
        Assert.Equal("Acme Legal LLP", tenant.Name);
        Assert.True(tenant.IsActive);

        // Same slug again: a clean conflict, not a duplicate.
        using var duplicate = await operatorClient.PostAsJsonAsync("/api/admin/tenants",
            new { name = "Acme Again", slug });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Theory]
    [InlineData("", "no-name")]
    [InlineData("Bad Slug Inc", "Has Spaces")]
    [InlineData("Bad Slug Inc", "UPPER!")]
    public async Task CreateTenant_RejectsInvalidInput(string name, string slug)
    {
        using var response = await fixture.ClientFor("system_admin")
            .PostAsJsonAsync("/api/admin/tenants", new { name, slug });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_IsOperatorOnly()
    {
        // tenant_admin manages ONE tenant; creating tenants is cross-tenant (platform.tenants.manage).
        using var response = await fixture.ClientFor("tenant_admin")
            .PostAsJsonAsync("/api/admin/tenants", new { name = "Nope", slug = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record TenantDto(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt);
}
