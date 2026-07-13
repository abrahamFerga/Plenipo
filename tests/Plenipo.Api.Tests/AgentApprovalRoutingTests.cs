using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of human-in-the-loop control for side-effecting tools. A tool marked
/// <c>RequiresApproval</c> must be BLOCKED mid-turn — surfaced as an <c>ApprovalRequired</c> event and recorded
/// as a pending approval — rather than executed. This is the safety mechanism for destructive actions: the
/// agent can request them, but a human authorizes them. The Mock client requests the tool; the runner must
/// refuse to run it.
/// </summary>
public sealed class AgentApprovalRoutingTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AgentApprovalRoutingTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_side_effecting_tool_is_routed_to_approval_not_executed()
    {
        var client = ClientAs("system_admin", "hitl-op");

        var response = await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "please use the record tool" });
        response.EnsureSuccessStatusCode();
        var events = (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;

        // The turn surfaces the blocked action for human review...
        Assert.Contains(events, e => e.Type == "ApprovalRequired" && e.ToolName == "record");

        // ...and it now sits in the approvals queue awaiting a decision (it did NOT execute).
        var approvals = await client.GetFromJsonAsync<List<ApprovalDto>>("/api/chat/approvals");
        Assert.Contains(approvals!, a => a.ToolName == "record");
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    private sealed record ApprovalDto(
        Guid Id, Guid ConversationId, string ModuleId, string ToolName, string? ArgumentsJson, string? UserDisplay, DateTimeOffset CreatedAt);
}
