using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.Application.Mcp;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Mcp;

/// <summary>
/// Surfaces MCP-discovered tools to every module's agent under the <c>mcp</c> pseudo-module. The
/// same security spine applies as to native tools: each tool is permission-gated
/// (<c>tools.mcp.{server}_{tool}</c> — granted to no role by default), audited, and blocked for
/// human approval when the server is configured <c>RequiresApproval</c> (the default). Only
/// registered when <c>Mcp:Servers</c> is non-empty.
/// </summary>
public sealed class McpToolSource : IPlatformToolSource
{
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var provider = scopedServices.GetRequiredService<IMcpToolProvider>();

        return provider.GetTools()
            .Select(tool => new ModuleTool
            {
                ModuleId = Permissions.McpToolModule,
                Name = tool.Function.Name,
                Permission = Permissions.ForTool(Permissions.McpToolModule, tool.Function.Name),
                Function = tool.Function,
                RequiresApproval = tool.RequiresApproval,
            })
            .ToList();
    }
}
