using Cortex.Core.Entities;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// A billable (or non-billable) time entry on a matter — quick-capture from chat ("log 0.5h on
/// Acme for the NDA call"). Deliberately append-only and low-ceremony: capture friction is why
/// lawyers under-record time, so logging is NOT approval-gated (unlike other module writes);
/// entries are own-user, low-stakes, and correctable by a follow-up entry.
/// </summary>
public sealed class TimeEntry : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>Who did the work (the logging user).</summary>
    public Guid? UserId { get; set; }

    /// <summary>Display-name snapshot at log time, so the Time tab reads without a user join.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Hours worked (decimal, e.g. 0.5). Bounded to a day per entry.</summary>
    public decimal Hours { get; set; }

    /// <summary>What was done — becomes the narrative line on the bill.</summary>
    public required string Description { get; set; }

    /// <summary>The day the work happened (defaults to today at log time).</summary>
    public DateOnly WorkedOn { get; set; }

    public bool Billable { get; set; } = true;
}
