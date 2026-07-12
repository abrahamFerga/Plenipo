using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Cortex.Api.Tests;

/// <summary>
/// End-to-end coverage of the admin write-path input validation added this session — exercised through the real
/// HTTP pipeline as the operator (so these assert VALIDATION, not authorization). Complements the unit tests on
/// the pure validators (<c>PermissionGrantValidator</c>, <c>TenantAiSettingsValidator</c>) by proving the
/// endpoints actually return a clean 400 rather than persisting bad input or 500-ing at the database.
/// </summary>
public sealed class AdminValidationTests : IClassFixture<CortexApiFactory>
{
    private readonly CortexApiFactory _factory;

    public AdminValidationTests(CortexApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task Creating_a_role_with_an_unknown_permission_is_rejected()
    {
        var client = Operator("val-unknown-perm");

        var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new { role = "typo_role", permissions = new[] { "platform.users.mange" } }); // typo of ...manage

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("catalog", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AiSettings_rejects_a_negative_conversation_token_budget()
    {
        var client = Operator("val-neg-budget");

        var response = await client.PutAsJsonAsync(
            "/api/admin/ai-settings",
            new { systemPrompt = (string?)null, maxConversationTokens = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AiSettings_rejects_a_system_prompt_longer_than_the_column()
    {
        var client = Operator("val-long-prompt");

        var response = await client.PutAsJsonAsync(
            "/api/admin/ai-settings",
            new { systemPrompt = new string('a', 8001), maxConversationTokens = (int?)null });

        // Without endpoint validation this would 500 at SaveChanges (varchar(8000) overflow); it must be a 400.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AiSettings_accepts_a_valid_override()
    {
        var client = Operator("val-happy");

        var response = await client.PutAsJsonAsync(
            "/api/admin/ai-settings",
            new { systemPrompt = "You are a concise assistant.", maxConversationTokens = 5000 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AiModels_requires_a_key_before_contacting_OpenAI()
    {
        var client = Operator("val-models-key");

        var response = await client.PostAsJsonAsync(
            "/api/admin/ai-models",
            new { provider = "OpenAI", endpoint = (string?)null, apiKey = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("API key", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AiModels_explains_Azure_deployment_names_without_a_hardcoded_catalog()
    {
        var client = Operator("val-models-azure");

        var response = await client.PostAsJsonAsync(
            "/api/admin/ai-models",
            new { provider = "AzureOpenAI", endpoint = "https://example.openai.azure.com", apiKey = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("deployment name", body, StringComparison.OrdinalIgnoreCase);
    }
}
