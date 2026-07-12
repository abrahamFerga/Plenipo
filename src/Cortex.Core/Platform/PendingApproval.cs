using Cortex.Core.Entities;

namespace Cortex.Core.Platform;

/// <summary>Lifecycle of a side-effecting tool call that the agent was blocked from auto-executing.</summary>
public enum ApprovalStatus
{
    /// <summary>Awaiting a human decision.</summary>
    Pending = 0,

    /// <summary>Approved and the tool ran successfully.</summary>
    Executed = 1,

    /// <summary>A human declined the action.</summary>
    Rejected = 2,

    /// <summary>Approved, but the tool threw when it ran.</summary>
    Failed = 3,
}

/// <summary>
/// A record of a side-effecting agent tool call that was blocked pending human approval (see
/// <c>ToolInvocationMiddleware</c>). The recorded arguments let an authorized human approve the action
/// later, at which point the platform re-executes that exact tool call. This is the second half of the
/// human-in-the-loop control: the first half blocks; this lets a human complete or decline the action.
/// </summary>
public sealed class PendingApproval : TenantEntityBase
{
    /// <summary>The user whose conversation triggered the blocked tool call.</summary>
    public Guid? UserId { get; set; }

    public string? UserDisplay { get; set; }

    public Guid ConversationId { get; set; }

    public required string ModuleId { get; set; }

    public required string ToolName { get; set; }

    /// <summary>The serialized arguments the agent intended to call the tool with.</summary>
    public string? ArgumentsJson { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>The user who approved or rejected the action — the "approved by whom" half of the
    /// oversight record that an automated-decision (ADMT) disclosure has to be able to answer.</summary>
    public Guid? ResolvedByUserId { get; set; }

    /// <summary>Display name of the resolver, captured at resolve time for audit attribution (the
    /// same best-effort convention as <see cref="UserDisplay"/>).</summary>
    public string? ResolvedByDisplay { get; set; }

    /// <summary>The tool's (free-text) result when approved + executed.</summary>
    public string? Result { get; set; }

    /// <summary>The error message when execution failed.</summary>
    public string? Error { get; set; }
}
