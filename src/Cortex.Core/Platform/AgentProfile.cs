using Cortex.Core.Entities;
using Cortex.Core.Multitenancy;

namespace Cortex.Core.Platform;

/// <summary>
/// How a profile's instructions combine with the module manifest's built-in agent instructions.
/// </summary>
public enum AgentProfileMode
{
    /// <summary>Manifest instructions stay; the profile's are added after them (specialize).</summary>
    Append = 0,

    /// <summary>The profile's instructions replace the manifest's entirely (rebrand/retask).</summary>
    Replace = 1,
}

/// <summary>
/// A named, tenant-configurable chatbot personality for a module: its own instructions layered
/// onto (or replacing) the module's built-in ones. The per-module DEFAULT profile is what the
/// runner applies on every turn — so an admin can retask "the legal assistant" without a code
/// change, per tenant. Editing is admin-surface only (permission <c>platform.ai.manage</c>) and
/// auto-audited like every entity change.
/// </summary>
public sealed class AgentProfile : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The module whose agent this profile configures (e.g. "legal").</summary>
    public required string ModuleId { get; set; }

    /// <summary>Display name, unique per module within the tenant (e.g. "Litigation voice").</summary>
    public required string Name { get; set; }

    public required string Instructions { get; set; }

    public AgentProfileMode Mode { get; set; } = AgentProfileMode.Append;

    /// <summary>At most one default per (tenant, module); the default is the one the runner applies.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Which tools this agent may use, as tool-name patterns (exact names, or a trailing <c>*</c>
    /// wildcard like <c>ask_*</c>). Null or empty = every tool the caller is permitted to call
    /// (the pre-profile behaviour). A selection can only NARROW the RBAC-permitted set — it never
    /// grants a tool the user's permissions don't already allow.
    /// </summary>
    public List<string>? ToolNames { get; set; }
}
