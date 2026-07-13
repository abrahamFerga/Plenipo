using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Host-declared product roles end to end (improvement loop it5): the sample host declares
/// "paralegal"; a freshly provisioned tenant lists it with its baseline alongside the built-ins,
/// and a user assigned the role resolves exactly that baseline — no fork, one AddPlenipoRole call.
/// </summary>
[Collection("api")]
public sealed class ProductRoleTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ProductRole_SeedsIntoNewTenants_AndGrantsItsBaseline()
    {
        // Provision a fresh tenant — its role table seeds from the MERGED baseline.
        using var operator_ = fixture.ClientFor("system_admin");
        (await operator_.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Paralegal Test LLP",
            slug = "paralegal-co",
            adminEmail = "admin@paralegal.example",
            adminSubject = "paralegal-co-admin",
        })).EnsureSuccessStatusCode();

        var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "paralegal-co-admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "paralegal-co");

        // The role list shows paralegal with its host-declared baseline.
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");
        var paralegal = roles.EnumerateArray().First(r => r.GetProperty("role").GetString() == "paralegal");
        var baseline = paralegal.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("tools.legal.list_deadlines", baseline);
        Assert.Contains("legal.matters.view", baseline);
        Assert.True(paralegal.GetProperty("editable").GetBoolean()); // tenant admins own it after seeding

        // A user signs in with the paralegal role (as their IdP would assert) → the baseline applies.
        var paralegalUser = fixture.Factory.CreateClient();
        paralegalUser.DefaultRequestHeaders.Add("X-Dev-Subject", "paralegal-1");
        paralegalUser.DefaultRequestHeaders.Add("X-Dev-Tenant", "paralegal-co");
        paralegalUser.DefaultRequestHeaders.Add("X-Dev-Roles", "paralegal");

        var me = await paralegalUser.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("tools.legal.add_deadline", permissions);
        Assert.Contains("chat.use", permissions);
        Assert.DoesNotContain(permissions, p => p!.StartsWith("platform.", StringComparison.Ordinal)); // no admin surface
    }
}
