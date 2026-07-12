namespace Cortex.Modules.Sdk;

/// <summary>
/// How often a recurring job fires. Deliberately a closed, coarse set rather than a cron
/// expression: recurring platform work (digests, reminder sweeps, retention passes) needs "about
/// once per window", not minute-precision — and every cadence added here must be reasoned about
/// in the catch-up contract on <see cref="RecurringJobDescriptor"/>. Products that outgrow these
/// windows should enqueue their own follow-up work from inside a handler instead.
/// </summary>
public enum RecurringJobCadence
{
    /// <summary>Once per hour.</summary>
    Hourly = 0,

    /// <summary>Once per day — the digest cadence.</summary>
    Daily = 1,

    /// <summary>Once per week.</summary>
    Weekly = 2,
}

/// <summary>
/// Recurring background work a module declares manifest-first (like tools and tabs): the module
/// supplies the logic — an <c>IJobHandler</c> registered for <see cref="Kind"/> — and the platform
/// supplies the schedule, enqueuing one job per enabled tenant per cadence window. A daily
/// bill-reminder digest is the canonical example.
///
/// <para><b>Identity.</b> A recurring run has no enqueuing user, so it executes under the
/// platform's tenant-scoped system principal: tenant filters and module data access work exactly
/// as in a user-enqueued job, the permission snapshot is the module's tool wildcard
/// (<c>tools.{moduleId}.*</c>), and audit rows attribute the run to the system scheduler with the
/// tenant recorded and no user id. Completion notifications are skipped — there is no enqueuer to
/// tell; a handler that wants to reach humans emits its own notifications (that is usually the
/// point of the job).</para>
///
/// <para><b>Missed-window contract (catch-up-one).</b> The platform persists the last enqueue
/// time per (tenant, kind). A job is due when a full cadence interval has elapsed since that
/// stamp — or immediately when no stamp exists yet (a new tenant or a newly shipped module gets
/// its first run on the next sweep, not after a silent first window). If the host was down past
/// one or more windows, the next sweep enqueues exactly ONE catch-up run — never one per missed
/// window — and the schedule re-anchors at that run. Restarts therefore neither double-fire nor
/// skip forever, at the cost of the window drifting by the downtime; wall-clock alignment
/// ("always 06:00") is a non-goal.</para>
/// </summary>
/// <param name="Kind">
/// The job kind to enqueue, dispatched through the platform's <c>IJobHandler</c> registry —
/// conventionally <c>"{moduleId}.{job-name}"</c>. Must be globally unique across all installed
/// modules (validated at startup): the scheduler's last-run stamp and the handler registry both
/// key on kind alone.
/// </param>
/// <param name="Cadence">How often each tenant gets a run. See the catch-up contract above.</param>
/// <param name="Description">
/// Human-facing sentence for operators ("Sends the daily bill-reminder digest."). Required — a
/// schedule that fires on its own must be explainable without reading handler code.
/// </param>
public sealed record RecurringJobDescriptor(string Kind, RecurringJobCadence Cadence, string Description);
