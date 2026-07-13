namespace Plenipo.Modules.Sdk;

/// <summary>
/// Static, manifest-level declaration of a tool a module exposes to the agent. Declared before any
/// module code runs so the platform can reason about capabilities, permissions, and audit policy
/// without loading the implementation. The executable counterpart is <c>ModuleTool</c>.
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>Tool name as the model sees it. Must match the registered <c>ModuleTool.Name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Human + model facing description. Drives tool selection — be specific, never vague.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Permission string required to call this tool. The agent runner filters tools by this
    /// <em>before</em> the model call, so a user without it never sees the tool's schema.
    /// </summary>
    public required string Permission { get; init; }

    /// <summary>When true the tool is side-effecting and requires human approval before execution.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>When true every invocation is written to the audit log. Defaults to on.</summary>
    public bool Audit { get; init; } = true;
}
