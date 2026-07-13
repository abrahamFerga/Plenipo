using System.Net.Http.Json;
using Plenipo.Sample.Host.IntegrationTests.Evals;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// MCP tools through the full security spine: a discovered tool is callable by a permitted user
/// (system_admin's wildcard), side-effecting servers hit the human-approval gate, and no built-in
/// role grants tools.mcp.* — an ordinary user's model never even sees the tool.
/// </summary>
[Collection("api")]
public sealed class McpToolTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task PermittedUser_CallsAnMcpTool_ThroughTheNormalPipeline()
    {
        using var client = ClientFor("system_admin");
        var run = await ChatAsync(client, "Echo message hello from the integration test");

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("fake_echo_message", run.ToolCalls);
        Assert.DoesNotContain("approval_required", run.CustomEvents); // echo server opted out of the gate
    }

    [Fact]
    public async Task ApprovalGatedServer_ToolIsBlockedPendingHumanApproval()
    {
        using var client = ClientFor("system_admin");
        var run = await ChatAsync(client, "Send alert about the database outage");

        Assert.Contains("fake_send_alert", run.ToolCalls);
        Assert.Contains("approval_required", run.CustomEvents);
    }

    [Fact]
    public async Task NoBuiltInRole_GrantsMcpTools_ByDefault()
    {
        // 'user' can chat, but tools.mcp.* is granted to no role — the tool is filtered out before
        // the model call, so the same message produces no MCP tool call.
        using var client = ClientFor("user");
        var run = await ChatAsync(client, "Echo message hello from the integration test");

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.DoesNotContain("fake_echo_message", run.ToolCalls);
    }

    private HttpClient ClientFor(string role)
    {
        var client = fixture.McpFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-mcp-{role}");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    private static async Task<EvalRun> ChatAsync(HttpClient client, string message)
    {
        using var chat = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { id = "m1", role = "user", content = message } } });
        chat.EnsureSuccessStatusCode();
        return EvalRun.Parse(await chat.Content.ReadAsStringAsync());
    }
}
