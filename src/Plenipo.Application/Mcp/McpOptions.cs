namespace Plenipo.Application.Mcp;

/// <summary>
/// External MCP tool servers (config section <c>Mcp</c>). Like skills, MCP servers are
/// DEPLOY-TIME configuration — the host operator decides which servers exist; tenants never add
/// arbitrary ones at runtime. Every discovered tool still flows through the normal security spine:
/// RBAC-gated as <c>tools.mcp.{server}_{tool}</c>, audited, and (by default) approval-gated.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public List<McpServerConfig> Servers { get; set; } = [];
}

/// <summary>One configured MCP server: a subprocess (stdio) or a remote Streamable HTTP endpoint.</summary>
public sealed class McpServerConfig
{
    /// <summary>Short identifier used to prefix tool names and permissions (e.g. "github").</summary>
    public string Name { get; set; } = "";

    /// <summary>"Stdio" (local subprocess) or "Http" (remote Streamable HTTP endpoint).</summary>
    public string Transport { get; set; } = "Stdio";

    /// <summary>Stdio: the executable to launch (e.g. "npx").</summary>
    public string? Command { get; set; }

    /// <summary>Stdio: arguments for the executable.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Http: the server endpoint URL.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether this server's tools are held for human approval before executing. Defaults to TRUE:
    /// an MCP server is external code whose side effects Plenipo cannot classify, so the safe
    /// default is the approval gate; opt read-only servers out explicitly.
    /// </summary>
    public bool RequiresApproval { get; set; } = true;
}
