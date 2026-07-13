using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The one-call provisioning orchestrator (docs/COMMERCIALIZATION.md phase 2): tenant + first
/// admin + licensed modules + AI budget + seat limit in a single transaction. This is the target
/// a billing webhook calls; until then it's the operator's one-liner.
/// </summary>
[Collection("api")]
public sealed class TenantProvisioningTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Provision_CreatesTenantAdminModulesBudgetAndSeats_InOneCall()
    {
        using var operator_ = fixture.ClientFor("system_admin");
        using var created = await operator_.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Vandelay Legal",
            slug = "vandelay-legal",
            adminEmail = "admin@vandelay.example",
            adminSubject = "vandelay-admin",
            modules = new[] { "legal" },
            maxSeats = 5,
            monthlyTokenBudget = 1_000_000L,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vandelay-legal", dto.GetProperty("slug").GetString());
        Assert.Equal(5, dto.GetProperty("maxSeats").GetInt32());
        Assert.Equal(new[] { "legal" }, dto.GetProperty("enabledModules").EnumerateArray().Select(m => m.GetString()));

        // The admin signs in (dev auth: subject header) and lands as tenant_admin of the new
        // tenant: they can see admin surfaces, and only the licensed module shows.
        var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "vandelay-admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "vandelay-legal");
        // No X-Dev-Roles: authorization must come from the PROVISIONED role assignment.

        var modules = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var ids = modules.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        Assert.Contains("legal", ids);
        Assert.DoesNotContain("finance", ids);  // not licensed → disabled at provisioning
        Assert.DoesNotContain("nutrition", ids);

        // tenant_admin baseline includes user management — the provisioned role really applies.
        using var users = await admin.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);

        // The AI settings carry the metered monthly budget.
        var ai = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai-settings");
        Assert.Equal(1_000_000L, ai.GetProperty("maxMonthlyTokensOverride").GetInt64());
    }

    [Fact]
    public async Task Provision_ValidatesInput_AndRefusesDuplicateSlug()
    {
        using var operator_ = fixture.ClientFor("system_admin");

        using var missing = await operator_.PostAsJsonAsync("/api/admin/tenants/provision",
            new { name = "X", slug = "x-co" }); // no adminEmail
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var badModule = await operator_.PostAsJsonAsync("/api/admin/tenants/provision",
            new { name = "X", slug = "x-co", adminEmail = "a@x.example", modules = new[] { "no-such-module" } });
        Assert.Equal(HttpStatusCode.BadRequest, badModule.StatusCode);

        using var badSeats = await operator_.PostAsJsonAsync("/api/admin/tenants/provision",
            new { name = "X", slug = "x-co", adminEmail = "a@x.example", maxSeats = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, badSeats.StatusCode);

        using var dupe = await operator_.PostAsJsonAsync("/api/admin/tenants/provision",
            new { name = "Dev again", slug = "dev", adminEmail = "a@x.example" });
        Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);

        // And it is operator-gated like tenant creation.
        using var denied = await fixture.ClientFor("tenant_admin").PostAsJsonAsync("/api/admin/tenants/provision",
            new { name = "Sneaky", slug = "sneaky-co", adminEmail = "s@x.example" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
}
