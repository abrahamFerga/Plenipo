using Microsoft.Extensions.AI;

namespace Plenipo.Modules.Sdk;

/// <summary>
/// How consequential approving this tool call is — a REVIEW-SURFACE rendering hint, never a gate.
/// Every <see cref="ModuleTool.RequiresApproval"/> tool still blocks and waits for a human either
/// way; risk only decides how much ceremony the review UI gives it. Uniform ceremony trains
/// reviewers to rubber-stamp: when a $4 coffee categorization and a budget rewrite look equally
/// alarming, people stop reading both.
/// </summary>
public enum ApprovalRisk
{
    /// <summary>
    /// Consequential (the default — nothing downgrades silently): changes money, membership,
    /// standing configuration, or anything hard to undo. Gets the full review card.
    /// </summary>
    High = 0,

    /// <summary>
    /// Routine and cheaply reversible (categorize one transaction, tag a record): renders as a
    /// compact one-tap confirm.
    /// </summary>
    Low = 1,
}

/// <summary>
/// The executable counterpart of a <see cref="ToolDescriptor"/>: a concrete <see cref="AIFunction"/>
/// coupled to the permission required to call it and the module that owns it. The agent runner builds
/// the per-request tool set from these, filtering by permission before the model ever sees a schema.
/// </summary>
public sealed class ModuleTool
{
    public required string ModuleId { get; init; }

    /// <summary>Must equal the underlying <see cref="AIFunction"/> name and the declaring descriptor's name.</summary>
    public required string Name { get; init; }

    /// <summary>Permission gating this tool. Checked pre-model-call.</summary>
    public required string Permission { get; init; }

    /// <summary>The invocable function handed to the agent.</summary>
    public required AIFunction Function { get; init; }

    /// <summary>Side-effecting tool that should be wrapped for human approval.</summary>
    public bool RequiresApproval { get; init; }

    /// <summary>
    /// Review-surface ceremony for this tool's pending approvals (meaningful only with
    /// <see cref="RequiresApproval"/>). Defaults to <see cref="ApprovalRisk.High"/>.
    /// </summary>
    public ApprovalRisk Risk { get; init; } = ApprovalRisk.High;

    /// <summary>Whether invocations are audited. Defaults to on.</summary>
    public bool Audit { get; init; } = true;
}
