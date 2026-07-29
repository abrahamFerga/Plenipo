using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The read surfaces that report which AI provider is in play must report the TENANT's, not the
/// deployment's. Both were written when AI configuration was deployment-only; runtime per-tenant
/// provider switching landed days later and neither caught up, so a tenant that configured a real
/// provider kept being told it was in demo mode, and an admin reading the ops card saw a provider
/// its tenant was not using beside token spend that was real.
///
/// The fixture's deployment default is Mock (PlenipoApiFactory), and each test class gets its own
/// in-memory store — so a tenant override here contrasts cleanly with the deployment and leaks
/// into no other class.
/// </summary>
public sealed class TenantEffectiveAiSurfaceTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public TenantEffectiveAiSurfaceTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject, string tenant = "dev")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        return client;
    }

    [Fact]
    public async Task Platform_info_reports_the_tenants_provider_not_the_deployments()
    {
        var client = Operator("ai-surface-info");

        // Tests in a class share one store, so establish the starting state rather than assume it:
        // clear every override, which puts the tenant back on the deployment connection.
        var clear = await client.PutAsJsonAsync("/api/admin/ai-settings", new { provider = (string?)null });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        // With nothing overridden, the deployment's Mock provider IS the tenant's answer.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/platform/info");
        Assert.True(before.GetProperty("demoMode").GetBoolean());
        Assert.True(before.GetProperty("chatEnabled").GetBoolean());

        // The tenant turns chat off for itself. "None" is a provider a tenant may switch to.
        var save = await client.PutAsJsonAsync("/api/admin/ai-settings", new { provider = "None" });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/platform/info");
        // chatEnabled is the serious half: left deployment-scoped, the shell keeps offering a Chat
        // tab and the tenant only learns the truth mid-turn, from the runner.
        Assert.False(after.GetProperty("chatEnabled").GetBoolean());
        Assert.False(after.GetProperty("demoMode").GetBoolean());
    }

    [Fact]
    public async Task Platform_info_stops_claiming_demo_mode_once_the_tenant_configures_a_real_provider()
    {
        var client = Operator("ai-surface-demo");

        var save = await client.PutAsJsonAsync("/api/admin/ai-settings", new
        {
            provider = "Ollama",
            model = "llama3.1",
            endpoint = "http://localhost:11434/v1",
        });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var info = await client.GetFromJsonAsync<JsonElement>("/api/platform/info");

        // The banner's own contract is "renders nothing once a real provider is configured".
        Assert.False(info.GetProperty("demoMode").GetBoolean());
        Assert.True(info.GetProperty("chatEnabled").GetBoolean());
    }

    [Fact]
    public async Task The_ops_ai_card_is_tenant_effective_in_every_field()
    {
        var client = Operator("ai-surface-ops");

        var save = await client.PutAsJsonAsync("/api/admin/ai-settings", new
        {
            provider = "Ollama",
            model = "llama3.1",
            endpoint = "http://localhost:11434/v1",
            maxMonthlyTokens = 4242L,
        });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var ops = await client.GetFromJsonAsync<JsonElement>("/api/admin/ops");
        var ai = ops.GetProperty("ai");

        // Provider and model used to come from the deployment options while the budget beside them
        // came from the tenant — one unlabelled card contradicting itself at a glance.
        Assert.Equal("Ollama", ai.GetProperty("provider").GetString());
        Assert.Equal("llama3.1", ai.GetProperty("model").GetString());
        Assert.Equal(4242L, ai.GetProperty("maxMonthlyTokens").GetInt64());
    }

    [Fact]
    public async Task A_model_only_override_is_still_reported_as_the_tenants_model()
    {
        var client = Operator("ai-surface-model-only");

        // The quiet case: no provider override, only a model. Merge honours the model and inherits
        // the provider, so the card must show the tenant's model against the deployment's provider.
        var save = await client.PutAsJsonAsync("/api/admin/ai-settings", new { model = "gpt-4o-mini-custom" });
        Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);

        var ops = await client.GetFromJsonAsync<JsonElement>("/api/admin/ops");
        var ai = ops.GetProperty("ai");

        Assert.Equal("gpt-4o-mini-custom", ai.GetProperty("model").GetString());
        Assert.Equal("Mock", ai.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Info_falls_back_to_the_deployment_when_no_tenant_resolves()
    {
        // Guard, green before and after: /info needs authentication but no permission, so an
        // authenticated caller whose tenant slug does not resolve reaches the handler with no
        // tenant. The answer must be the deployment's rather than a crash or an empty one.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "ai-surface-no-tenant");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "no-such-tenant-slug");

        // Deliberately no reset here: this caller has no tenant at all, so no override can apply
        // to it whatever the dev tenant's row happens to say.
        var response = await client.GetAsync("/api/platform/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(info.GetProperty("demoMode").GetBoolean());
        Assert.True(info.GetProperty("chatEnabled").GetBoolean());
    }
}
