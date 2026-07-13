using Plenipo.Core.Entities;

namespace Plenipo.Modules.Legal.Persistence;

/// <summary>
/// A to-do on a matter — the working checklist that sits beside the docketed deadlines ("draft
/// the motion", "call opposing counsel", "collect the exhibits"). Tasks carry a free-text
/// assignee (real shops assign to a person by name, not an account id) and an optional due date;
/// hard dates with reminder obligations belong in <see cref="MatterDeadline"/> instead.
/// </summary>
public sealed class MatterTask : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public required string Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>Who it's assigned to, as displayed (e.g. "Maria", "paralegal team").</summary>
    public string? AssignedTo { get; set; }

    /// <summary>Optional target date — softer than a docketed deadline; no reminder machinery.</summary>
    public DateOnly? DueOn { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Who created the task (the chat caller).</summary>
    public Guid? CreatedByUserId { get; set; }
}
