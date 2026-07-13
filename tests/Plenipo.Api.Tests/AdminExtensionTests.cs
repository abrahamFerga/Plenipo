using System.Text.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The admin console's extension surface: a module declares admin pages in its manifest and they
/// appear at <c>/api/admin/extensions</c> — permission-filtered exactly like domain tabs, so a
/// caller without the page's permission never learns it exists.
/// </summary>
public sealed class AdminExtensionTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AdminExtensionTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_module_declared_admin_page_is_served_to_a_caller_holding_its_permission()
    {
        // system_admin holds "*", which covers the test module's admin-page permission.
        using var client = ClientAs("system_admin", "ext-admin");
        var response = await client.GetAsync("/api/admin/extensions");
        response.EnsureSuccessStatusCode();

        var extensions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var test = extensions.EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == "test");
        Assert.Equal("Test Module", test.GetProperty("displayName").GetString());

        var tab = Assert.Single(test.GetProperty("tabs").EnumerateArray());
        Assert.Equal("widgets", tab.GetProperty("id").GetString());
        Assert.Equal("Widget registry", tab.GetProperty("label").GetString());
        Assert.Equal("/api/test/widgets", tab.GetProperty("dataEndpoint").GetString());
        // The same wire shape as domain tabs — the admin app reuses the generic renderer as-is.
        Assert.Equal(2, tab.GetProperty("columns").GetArrayLength());
    }

    [Fact]
    public async Task A_caller_without_the_pages_permission_never_learns_it_exists()
    {
        using var client = ClientAs("user", "ext-plain-user");
        var response = await client.GetAsync("/api/admin/extensions");
        response.EnsureSuccessStatusCode();

        // Not a 403 — the listing itself is harmless — but the module (whose only admin tab the
        // caller can't see) is omitted entirely, not returned with an empty tab list.
        var extensions = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.DoesNotContain(
            extensions.EnumerateArray(),
            e => e.GetProperty("id").GetString() == "test");
    }

}
