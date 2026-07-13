using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Per-tenant AI settings: overrides round-trip, and — the observable proof that the runner actually applies
/// them — a tiny per-tenant conversation token budget refuses further turns once exceeded. Gated by
/// platform.ai.manage. Runs in dedicated tenants so the low budget can't affect the shared dev tenant.
/// </summary>
[Collection("api")]
public sealed class AiSettingsIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Overrides_RoundTrip_AndExposeTheDefaults()
    {
        const string tenant = "ai-settings-crud";
        await fixture.EnsureTenantAsync(tenant);
        var admin = fixture.ClientForTenant("system_admin", tenant);

        using (var set = await admin.PutAsJsonAsync("/api/admin/ai-settings",
            new { systemPrompt = "Be terse.", maxConversationTokens = 5000 }))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai-settings");
        Assert.Equal("Be terse.", settings.GetProperty("systemPromptOverride").GetString());
        Assert.Equal(5000, settings.GetProperty("maxConversationTokensOverride").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(settings.GetProperty("defaultSystemPrompt").GetString()));

        // Clearing the overrides falls back to the defaults.
        using (var clear = await admin.PutAsJsonAsync("/api/admin/ai-settings",
            new { systemPrompt = (string?)null, maxConversationTokens = (int?)null }))
        {
            Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        }
        var cleared = await admin.GetFromJsonAsync<JsonElement>("/api/admin/ai-settings");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("systemPromptOverride").ValueKind);
    }

    [Fact]
    public async Task PerTenantTokenBudget_Override_IsEnforcedByTheAgentRunner()
    {
        const string tenant = "ai-budget";
        await fixture.EnsureTenantAsync(tenant);
        var client = fixture.ClientForTenant("system_admin", tenant);

        // A deliberately tiny conversation budget for this tenant.
        using (var set = await client.PutAsJsonAsync("/api/admin/ai-settings",
            new { systemPrompt = (string?)null, maxConversationTokens = 1 }))
        {
            Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        }

        var threadId = "budget-" + Guid.NewGuid().ToString("N")[..8];

        // The first turn runs (prior usage is zero)…
        using (var first = await client.PostAsJsonAsync("/api/agui/finance",
            new { threadId, messages = new[] { new { role = "user", content = "Hello there" } } }))
        {
            first.EnsureSuccessStatusCode();
            Assert.DoesNotContain("RUN_ERROR", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // …the second turn on the same conversation has already blown the 1-token budget and is refused.
        using (var second = await client.PostAsJsonAsync("/api/agui/finance",
            new { threadId, messages = new[] { new { role = "user", content = "And again" } } }))
        {
            second.EnsureSuccessStatusCode();
            var sse = await second.Content.ReadAsStringAsync();
            Assert.Contains("RUN_ERROR", sse, StringComparison.Ordinal);
            Assert.Contains("token budget", sse, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("system_admin", HttpStatusCode.OK)]
    public async Task AiSettings_IsGatedByManageAiSettings(string role, HttpStatusCode expected)
    {
        using var response = await fixture.ClientFor(role).GetAsync("/api/admin/ai-settings");
        Assert.Equal(expected, response.StatusCode);
    }
}
