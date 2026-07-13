using Plenipo.Application.Mcp;
using Plenipo.Infrastructure.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// MCP naming and mapping invariants: tool names are namespaced per server and clamped to the
/// permission-safe alphabet, and the tool source projects each discovered tool into a ModuleTool
/// whose permission string and approval flag the runner's security gates rely on.
/// </summary>
public sealed class McpToolSourceTests
{
    [Theory]
    [InlineData("github", "search_issues", "github_search_issues")]
    [InlineData("My Server", "Do.Things!", "my_server_do_things")]
    [InlineData("  fs  ", "read-file", "fs_read-file")]
    public void ToolNames_AreNamespacedAndSanitized(string server, string tool, string expected)
    {
        Assert.Equal(expected, McpClientManager.BuildToolName(server, tool));
    }

    [Fact]
    public void ToolSource_MapsDiscoveredTools_IntoPermissionGatedModuleTools()
    {
        var provider = new StubProvider(
        [
            new McpServerTool("github", AIFunctionFactory.Create(() => "ok", name: "github_search_issues"), RequiresApproval: false),
            new McpServerTool("jira", AIFunctionFactory.Create(() => "ok", name: "jira_create_ticket"), RequiresApproval: true),
        ]);
        var services = new ServiceCollection().AddSingleton<IMcpToolProvider>(provider).BuildServiceProvider();

        var tools = new McpToolSource().GetTools(services);

        Assert.Equal(2, tools.Count);

        var search = tools[0];
        Assert.Equal("mcp", search.ModuleId);
        Assert.Equal("github_search_issues", search.Name);
        Assert.Equal("tools.mcp.github_search_issues", search.Permission);
        Assert.False(search.RequiresApproval);

        var create = tools[1];
        Assert.Equal("tools.mcp.jira_create_ticket", create.Permission);
        Assert.True(create.RequiresApproval); // the flag the runner's approval gate reads

        services.Dispose();
    }

    private sealed class StubProvider(IReadOnlyList<McpServerTool> tools) : IMcpToolProvider
    {
        public IReadOnlyList<McpServerTool> GetTools() => tools;
    }
}
