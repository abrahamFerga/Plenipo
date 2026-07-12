using Cortex.Core.Entities;

namespace Cortex.Core.Platform;

/// <summary>
/// The scheduler's per-tenant watermark for one recurring job kind: when the platform last
/// enqueued it for this tenant. One row per (tenant, kind), stamped in the same save as the
/// enqueued job so a crash between the two can't happen — this is what makes restarts neither
/// double-fire (the stamp survives) nor skip forever (a stale stamp is simply "due" again),
/// giving the catch-up-one contract documented on <c>RecurringJobDescriptor</c>.
/// </summary>
public sealed class RecurringJobCursor : TenantEntityBase
{
    /// <summary>The recurring job kind (RecurringJobDescriptor.Kind, globally unique).</summary>
    public required string Kind { get; set; }

    /// <summary>When the scheduler last enqueued this kind for this tenant.</summary>
    public DateTimeOffset LastEnqueuedAt { get; set; }
}
