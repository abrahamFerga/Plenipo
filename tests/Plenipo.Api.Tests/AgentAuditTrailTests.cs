using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the audit trail: an executed (non-approval) tool call must be recorded so every
/// privileged action the agent takes is accountable after the fact. Runs a turn that actually invokes the echo
/// tool, then asserts the invocation surfaces in the admin audit log.
/// </summary>
public sealed class AgentAuditTrailTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AgentAuditTrailTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task An_executed_tool_call_is_recorded_in_the_audit_log()
    {
        var client = Operator("audit-op");

        var events = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "please use the echo tool" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        Assert.Contains(events, e => e.Type == "ToolInvoked" && e.ToolName == "echo");

        // The invocation is now accountable in the audit trail.
        var toolCalls = await client.GetFromJsonAsync<List<ToolCallDto>>("/api/admin/audit/tool-calls");
        Assert.Contains(toolCalls!, t => t.ToolName == "echo" && t.ModuleId == "test");
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    private sealed record ToolCallDto(Guid Id, string ModuleId, string ToolName, string Permission, bool Success);
}
