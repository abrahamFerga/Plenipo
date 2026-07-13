using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Per-tenant module enable/disable: a tenant admin can hide an installed module from the workspace, the
/// workspace catalog (which drives navigation) honours it, and the toggle is gated by platform.modules.manage.
/// Runs in its own tenant so disabling a module can't perturb the shared dev tenant.
/// </summary>
[Collection("api")]
public sealed class ModuleManagementIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task DisablingAModule_HidesItFromTheWorkspaceCatalog_AndReEnablingRestoresIt()
    {
        const string tenant = "module-mgmt";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // By default every installed module is visible.
        var before = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        Assert.Contains(before.EnumerateArray(), m => m.GetProperty("id").GetString() == "finance");

        // Disable finance for this tenant.
        using var disable = await admin.PutAsJsonAsync("/api/admin/modules/finance", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        // It disappears from the workspace catalog, but the others remain.
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        Assert.DoesNotContain(after.EnumerateArray(), m => m.GetProperty("id").GetString() == "finance");
        Assert.Contains(after.EnumerateArray(), m => m.GetProperty("id").GetString() == "nutrition");

        // The admin module list reflects the disabled state.
        var adminModules = await admin.GetFromJsonAsync<JsonElement>("/api/admin/modules");
        var finance = adminModules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "finance");
        Assert.False(finance.GetProperty("enabled").GetBoolean());

        // Re-enabling brings it back.
        using var reenable = await admin.PutAsJsonAsync("/api/admin/modules/finance", new { enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, reenable.StatusCode);
        var restored = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        Assert.Contains(restored.EnumerateArray(), m => m.GetProperty("id").GetString() == "finance");
    }

    [Fact]
    public async Task ChattingWithADisabledModule_IsRefused()
    {
        const string tenant = "module-chat-block";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Sanity: a chat turn to finance works while the module is enabled.
        using (var ok = await admin.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { role = "user", content = "Hello there" } } }))
        {
            ok.EnsureSuccessStatusCode();
            Assert.DoesNotContain("RUN_ERROR", await ok.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // Disable finance for this tenant.
        using var disable = await admin.PutAsJsonAsync("/api/admin/modules/finance", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        // A disabled module is uninvocable, not just hidden: the turn is refused before any tool or model
        // work, so no tool is ever called.
        using var blocked = await admin.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { role = "user", content = "Summarize my spending using a tool." } } });
        blocked.EnsureSuccessStatusCode(); // the SSE stream is established (200); the refusal is an in-stream RUN_ERROR
        var sse = await blocked.Content.ReadAsStringAsync();
        Assert.Contains("RUN_ERROR", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("TOOL_CALL_START", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisablingAModule_404sItsOwnEndpoints_AndReEnablingRestoresThem()
    {
        const string tenant = "module-endpoint-block";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        // Finance's own data endpoint responds while the module is enabled.
        using (var ok = await admin.GetAsync("/api/finance/transactions"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var disable = await admin.PutAsJsonAsync("/api/admin/modules/finance", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);

        // Disabled ⇒ uninvocable everywhere: the module's own endpoints now 404…
        using (var blocked = await admin.GetAsync("/api/finance/transactions"))
        {
            Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        }

        // …while a different, still-enabled module's endpoints keep working.
        using (var other = await admin.GetAsync("/api/nutrition/foods"))
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }

        using var reenable = await admin.PutAsJsonAsync("/api/admin/modules/finance", new { enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, reenable.StatusCode);
        using (var restored = await admin.GetAsync("/api/finance/transactions"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }
    }

    [Fact]
    public async Task TogglingAnUnknownModule_Returns404()
    {
        using var response = await fixture.ClientFor("system_admin")
            .PutAsJsonAsync("/api/admin/modules/not-a-real-module", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("system_admin", HttpStatusCode.OK)]
    public async Task AdminModulesList_IsGatedByManageModules(string role, HttpStatusCode expected)
    {
        using var response = await fixture.ClientFor(role).GetAsync("/api/admin/modules");
        Assert.Equal(expected, response.StatusCode);
    }
}
