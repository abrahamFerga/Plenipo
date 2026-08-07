using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the per-turn agent-run record. The guarantee under test is that EVERY turn
/// leaves exactly one row, however it ended — which is what the token-usage record cannot provide,
/// because a provider only reports usage for a turn it actually billed. A turn refused before the
/// model is reached produces no tokens at all, and those are precisely the turns an operator is
/// hunting for when the assistant "just didn't answer".
/// </summary>
public sealed class AgentRunRecordTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AgentRunRecordTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_completed_turn_records_exactly_one_run()
    {
        var client = Operator("run-op");

        var events = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "Hello" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        var conversationId = events.Single(e => e.Type == "Completed").ConversationId;
        Assert.NotNull(conversationId);

        var list = await client.GetFromJsonAsync<RunList>($"/api/admin/runs?conversationId={conversationId}");

        var run = Assert.Single(list!.Runs);
        Assert.Equal("Completed", run.Outcome);
        Assert.Equal("test", run.ModuleId);
        Assert.Null(run.ErrorKind);
        // The effective model is stamped even though the Mock provider serves the turn.
        Assert.False(string.IsNullOrEmpty(run.Model));
    }

    [Fact]
    public async Task A_turn_refused_before_the_model_still_records_a_run()
    {
        var client = Operator("run-op-unknown");

        var events = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "no-such-module", message = "Hello" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        Assert.Contains(events, e => e.Type == "Error");

        var list = await client.GetFromJsonAsync<RunList>("/api/admin/runs?module=no-such-module");

        var run = Assert.Single(list!.Runs);
        Assert.Equal("ModuleUnavailable", run.Outcome);
        Assert.Equal("UnknownModule", run.ErrorKind);
        // No model ever ran, so there are no tokens — the exact case token usage cannot surface.
        Assert.Equal(0, run.TotalTokens);

        // And the record is genuinely additional: the usage report knows nothing about this turn.
        var usage = await client.GetFromJsonAsync<UsageReport>("/api/admin/usage");
        Assert.DoesNotContain(usage!.ByModule, m => m.ModuleId == "no-such-module");
    }

    [Fact]
    public async Task The_run_detail_reconstructs_the_tool_calls_of_that_turn()
    {
        var client = Operator("run-op-tools");

        var events = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "please use the echo tool" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        var conversationId = events.Single(e => e.Type == "Completed").ConversationId;

        var list = await client.GetFromJsonAsync<RunList>($"/api/admin/runs?conversationId={conversationId}");
        var run = Assert.Single(list!.Runs);
        Assert.True(run.ToolCallCount > 0);

        var detail = await client.GetFromJsonAsync<RunDetail>($"/api/admin/runs/{run.Id}");
        Assert.Contains(detail!.ToolCalls, t => t.ToolName == "echo");
    }

    [Fact]
    public async Task Reading_runs_requires_the_audit_permission()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "member");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "run-op-nosy");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.GetAsync("/api/admin/runs");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    internal sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    internal sealed record RunDto(
        Guid Id, string ModuleId, string Outcome, string? ErrorKind, string? ErrorMessage,
        string? Model, long TotalTokens, int ToolCallCount, long TotalMs);

    internal sealed record RunSummary(int Total, int Errors, double ErrorRate, long P50Ms, long P95Ms, long TotalTokens);

    internal sealed record RunList(RunSummary Summary, RunDto[] Runs, string[] Modules, string[] Models, string[] Outcomes);

    internal sealed record RunToolCall(Guid Id, string ToolName, bool Success);

    internal sealed record RunDetail(RunDto Run, RunToolCall[] ToolCalls, RunDto[] Steps);

    internal sealed record UsageByModule(string ModuleId, long TotalTokens);

    internal sealed record UsageReport(long TotalTokens, int Turns, UsageByModule[] ByModule);
}

/// <summary>
/// The budget refusal deserves its own factory: capping the tenant's per-conversation budget would
/// otherwise leak into every other run test in the class. A refused turn is the sharpest case for
/// this record — it costs nothing, so nothing else in the platform remembers it happened.
/// </summary>
public sealed class AgentRunBudgetRecordTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AgentRunBudgetRecordTests(PlenipoApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_turn_refused_over_budget_is_recorded_as_a_run()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "run-budget-op");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var set = await client.PutAsJsonAsync(
            "/api/admin/ai-settings",
            new { systemPrompt = (string?)null, maxConversationTokens = 1 });
        set.EnsureSuccessStatusCode();

        // Turn 1 runs and spends well over the 1-token cap.
        var turn1 = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "Hello" })).Content
            .ReadFromJsonAsync<List<AgentRunRecordTests.StreamEvent>>())!;
        var conversationId = turn1.Single(e => e.Type == "Completed").ConversationId;

        // Turn 2 on the same conversation is refused before the model is reached.
        var turn2 = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", conversationId, message = "Again" })).Content
            .ReadFromJsonAsync<List<AgentRunRecordTests.StreamEvent>>())!;
        Assert.Contains(turn2, e => e.Type == "Error");

        var list = await client.GetFromJsonAsync<AgentRunRecordTests.RunList>(
            $"/api/admin/runs?conversationId={conversationId}");

        // Both turns are on the record: the one that ran, and the one that was refused.
        Assert.Equal(2, list!.Runs.Length);
        var refused = Assert.Single(list.Runs, r => r.Outcome == "BudgetExceeded");
        Assert.Equal("ConversationBudget", refused.ErrorKind);
        Assert.Contains("Completed", list.Runs.Select(r => r.Outcome));
    }
}
