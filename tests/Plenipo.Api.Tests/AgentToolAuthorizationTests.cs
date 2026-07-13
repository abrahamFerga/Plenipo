using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the agent's signature security property: the runner filters a module's tools by the
/// caller's permissions BEFORE the model call, so a user without a tool's permission never sees its schema and
/// can never invoke it. The Mock chat client performs a real tool call when asked, so the same prompt drives a
/// tool invocation for an authorized caller and none for an unauthorized one — proving the filter, not just the
/// gate. This is the most important thing the agent path does; a regression here would be a privilege breach.
/// </summary>
public sealed class AgentToolAuthorizationTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AgentToolAuthorizationTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private async Task<List<StreamEvent>> ChatAsync(HttpClient client, string message)
    {
        var response = await client.PostAsJsonAsync("/api/chat/stream", new { moduleId = "test", message });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;
    }

    [Fact]
    public async Task An_authorized_caller_can_have_the_permitted_tool_invoked()
    {
        // system_admin holds the global wildcard, so it is permitted tools.test.echo.
        var events = await ChatAsync(ClientAs("system_admin", "tool-op"), "please use the echo tool");

        Assert.Contains(events, e => e.Type == "ToolInvoked" && e.ToolName == "echo");
    }

    [Fact]
    public async Task An_unauthorized_caller_never_sees_or_invokes_the_tool()
    {
        // The plain user role holds chat.use but NOT tools.test.echo — the same prompt must not call the tool.
        var events = await ChatAsync(ClientAs("user", "tool-user"), "please use the echo tool");

        // The tool was filtered out before the model saw it, so it was never invoked — the security
        // guarantee. (The user baseline legitimately includes the platform document tools, so other
        // tool invocations may occur; the forbidden module tool must not.)
        Assert.DoesNotContain(events, e => e.Type == "ToolInvoked" && e.ToolName == "echo");
        // ...and the turn still completes normally (as plain text).
        Assert.Contains(events, e => e.Type == "Completed");
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);
}
