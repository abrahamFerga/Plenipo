namespace Plenipo.Modules.Sdk;

/// <summary>
/// A named, code-first agent a module ships: its own instructions, an optional tool selection, and
/// an optional model pin. Manifest agents appear in the chat's agent picker alongside any
/// admin-created profiles (a tenant profile with the same name overrides the manifest one, so
/// admins always win). Like profiles, a <see cref="ToolNames"/> selection can only NARROW the
/// caller's RBAC-permitted tool set — it never grants a tool.
/// </summary>
public sealed record AgentDescriptor
{
    /// <summary>Stable name within the module, unique across its agents and workflows (e.g. "drafter").</summary>
    public required string Name { get; init; }

    /// <summary>One line shown in the chat's agent picker.</summary>
    public string? Description { get; init; }

    /// <summary>This agent's instructions.</summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// When true the instructions REPLACE the module's <see cref="ModuleManifest.AgentInstructions"/>
    /// (retask); default appends after them (specialize).
    /// </summary>
    public bool ReplaceInstructions { get; init; }

    /// <summary>
    /// Tool-name patterns this agent may use (exact names or a trailing <c>*</c> wildcard).
    /// Null/empty = every tool the caller is permitted to call. Narrows RBAC, never grants.
    /// </summary>
    public IReadOnlyList<string>? ToolNames { get; init; }

    /// <summary>Model/deployment this agent runs on, within the tenant's provider. Null = default model.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// When true this agent applies when the user picks none — unless the tenant has its own
    /// default profile, which always wins.
    /// </summary>
    public bool IsDefault { get; init; }
}
