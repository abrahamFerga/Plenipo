using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Sample.Host.IntegrationTests.Evals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Runtime provider switching end to end: an admin changes the tenant's provider connection (and
/// vaulted API key) from the admin API, the very next turn runs on it, usage is attributed to the
/// EFFECTIVE provider/model (including an agent profile's per-agent model), and the key is
/// write-only — the API only ever reports that one exists.
/// </summary>
[Collection("api")]
public sealed class AiProviderSwitchTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task SwitchProvider_TakesEffectNextTurn_AndUsageRecordsTheEffectiveModel()
    {
        using var admin = fixture.ClientFor("system_admin");
        var marker = $"tenant-model-{Guid.NewGuid():N}"[..24];

        // Switch the tenant to its own connection (Mock keeps the test keyless/deterministic) with
        // a bespoke model name and an API key — the key goes to the vault, not the response.
        using var put = await admin.PutAsJsonAsync("/api/admin/ai-settings", new
        {
            systemPrompt = (string?)null,
            maxConversationTokens = (int?)null,
            maxMonthlyTokens = (long?)null,
            provider = "Mock",
            model = marker,
            apiKey = "sk-tenant-own-key",
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        try
        {
            // Write-only contract: the settings echo the provider/model but only a boolean for the key.
            using var get = await admin.GetAsync("/api/admin/ai-settings");
            var body = await get.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("Mock", doc.RootElement.GetProperty("providerOverride").GetString());
            Assert.Equal(marker, doc.RootElement.GetProperty("modelOverride").GetString());
            Assert.True(doc.RootElement.GetProperty("hasApiKey").GetBoolean());
            Assert.DoesNotContain("sk-tenant-own-key", body, StringComparison.Ordinal);

            // The next turn runs on the tenant connection; usage attributes spend to its model.
            var run = await ChatAsync(admin, "Hello from the provider-switch test");
            Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
            Assert.True(await UsageRowExistsAsync(marker), "expected a token-usage row for the tenant's model");
        }
        finally
        {
            // Reset to the deployment defaults and clear the key ("" = clear) — shared fixture.
            using var reset = await admin.PutAsJsonAsync("/api/admin/ai-settings", new
            {
                systemPrompt = (string?)null,
                maxConversationTokens = (int?)null,
                maxMonthlyTokens = (long?)null,
                apiKey = "",
            });
            reset.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task AgentProfileModel_OverridesTheModel_ForThatAgentsTurns()
    {
        using var admin = fixture.ClientFor("system_admin");
        var marker = $"agent-model-{Guid.NewGuid():N}"[..24];

        using var created = await admin.PutAsJsonAsync("/api/admin/agent-profiles", new
        {
            moduleId = "finance",
            name = "model-override-eval",
            instructions = "Answer briefly.",
            mode = "Append",
            isDefault = true,
            model = marker,
        });
        created.EnsureSuccessStatusCode();
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var profileId = createdBody.RootElement.GetProperty("id").GetGuid();

        try
        {
            var run = await ChatAsync(admin, "Hello from the profile-model test");
            Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
            Assert.True(await UsageRowExistsAsync(marker), "expected a token-usage row for the profile's model");
        }
        finally
        {
            using var deleted = await admin.DeleteAsync($"/api/admin/agent-profiles/{profileId}");
            deleted.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task SwitchingToOpenAI_WithoutAKey_IsRejected()
    {
        using var admin = fixture.ClientFor("system_admin");
        using var response = await admin.PutAsJsonAsync("/api/admin/ai-settings", new
        {
            provider = "OpenAI",
            model = "gpt-4o-mini",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("API key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<EvalRun> ChatAsync(HttpClient client, string message)
    {
        using var chat = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { id = "m1", role = "user", content = message } } });
        chat.EnsureSuccessStatusCode();
        return EvalRun.Parse(await chat.Content.ReadAsStringAsync());
    }

    private async Task<bool> UsageRowExistsAsync(string model)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await audit.TokenUsage.AnyAsync(u => u.Model == model);
    }
}
