namespace Plenipo.Modules.Sdk;

/// <summary>
/// A named multi-agent workflow a module ships: an ordered chain of the module's agents
/// (<see cref="ModuleManifest.Agents"/> or tenant profiles), selectable in the chat's picker like
/// an agent. Each step runs as a full authorized turn — the caller's RBAC, the tenant's budgets,
/// approval gates, and auditing apply to every step, never just the first — with the previous
/// step's output handed to the next. Steps must name agents; workflows do not nest.
/// </summary>
public sealed record WorkflowDescriptor
{
    /// <summary>Stable name within the module, sharing the agent picker namespace (e.g. "new-engagement").</summary>
    public required string Name { get; init; }

    /// <summary>One line shown in the chat's picker.</summary>
    public string? Description { get; init; }

    /// <summary>The agent names to run, in order (at least one).</summary>
    public required IReadOnlyList<string> AgentNames { get; init; }
}
