using Microsoft.Extensions.AI;

namespace Plenipo.Application.Mcp;

/// <summary>
/// A tool discovered from a configured MCP server, ready for the agent pipeline. The function is
/// already namespaced (<c>{server}_{tool}</c>) so names and permissions are collision-free across
/// servers.
/// </summary>
public sealed record McpServerTool(string ServerName, AIFunction Function, bool RequiresApproval);

/// <summary>
/// Supplies the current snapshot of MCP-discovered tools. Implemented by the infrastructure client
/// manager (which connects at startup and caches); a snapshot may legitimately be empty while
/// servers are still connecting or unreachable — MCP problems must never fail a chat turn.
/// </summary>
public interface IMcpToolProvider
{
    public IReadOnlyList<McpServerTool> GetTools();
}
