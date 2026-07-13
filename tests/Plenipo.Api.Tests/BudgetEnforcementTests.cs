using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the per-conversation token budget — the cost / availability guardrail that refuses
/// further turns once a conversation has consumed its cap (a defense against a runaway or abusive conversation
/// running up unbounded model cost). The codebase intended an integration test for this that never existed
/// because it needed a real database; the no-Docker harness makes it runnable. Uses its own factory instance, so
/// capping the budget doesn't affect other test classes.
/// </summary>
public sealed class BudgetEnforcementTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public BudgetEnforcementTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_conversation_that_reaches_its_token_budget_refuses_further_turns()
    {
        var client = Operator("budget-op");

        // Cap this tenant's per-conversation budget at a single token.
        var set = await client.PutAsJsonAsync(
            "/api/admin/ai-settings",
            new { systemPrompt = (string?)null, maxConversationTokens = 1 });
        set.EnsureSuccessStatusCode();

        // Turn 1: prior usage is 0, so it runs — and records this turn's usage (well over 1 token).
        var turn1 = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "Hello" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        var conversationId = turn1.Single(e => e.Type == "Completed").ConversationId;
        Assert.NotNull(conversationId);
        Assert.DoesNotContain(turn1, e => e.Type == "Error");

        // Turn 2 on the SAME conversation: prior usage now exceeds the 1-token budget → the turn is refused.
        var turn2 = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", conversationId, message = "Again" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;

        Assert.Contains(turn2, e =>
            e.Type == "Error" && (e.Error ?? string.Empty).Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);
}
