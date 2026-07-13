using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>
/// A long-running unit of work executed outside the request (bulk document review, batch imports,
/// scheduled work product). Enqueued by module code under the caller's identity; the job processor
/// restores that tenant + user context before executing, so tool authorization, query filters, and
/// audit apply inside jobs exactly as they do in a chat turn.
/// </summary>
public sealed class BackgroundJob : EntityBase, ITenantOwned
{
    /// <summary>
    /// The well-known <see cref="UserId"/> of the platform's system principal, used for jobs the
    /// platform itself enqueues (module-declared recurring work) where no human enqueuer exists.
    /// The processor recognizes it and runs the job tenant-scoped with NO user: audit rows carry
    /// the tenant and the scheduler's display identity but a null user id, and the completion
    /// notification is skipped (there is no enqueuer to notify). Safe as a sentinel because every
    /// real user id is a generated GUID v7 — never <see cref="Guid.Empty"/>.
    /// </summary>
    public static readonly Guid SystemUserId = Guid.Empty;

    public Guid TenantId { get; set; }

    /// <summary>
    /// The user whose authority the job runs under — or <see cref="SystemUserId"/> for a
    /// platform-scheduled run with no enqueuing user.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>The module that owns the handler (e.g. "legal").</summary>
    public required string ModuleId { get; set; }

    /// <summary>The registered handler kind (e.g. "legal.bulk-review").</summary>
    public required string Kind { get; set; }

    /// <summary>Handler input, serialized by the enqueuer.</summary>
    public required string ArgumentsJson { get; set; }

    /// <summary>
    /// The enqueuer's effective permissions, captured at enqueue time (JSON string array). The job
    /// executes with exactly this authority — roles asserted only in the user's token would otherwise
    /// be invisible to background execution, and capability capture also gives audit a faithful
    /// record of what the job was allowed to do.
    /// </summary>
    public required string PermissionsSnapshotJson { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>How many times the job has been claimed for execution (1 on the first run).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Set when the enqueuer asks to cancel a job that is already running. Cancellation is
    /// cooperative: the processor observes the flag at the job's next progress report.
    /// </summary>
    public bool CancelRequested { get; set; }

    /// <summary>
    /// The claim lease, stamped when the processor takes the job and extended at every progress
    /// report. A Running job whose lease has expired was orphaned by a crashed or stopped host;
    /// the processor requeues it (or fails it once its attempts are used up).
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>0–100. Handlers report progress; the UI polls it.</summary>
    public int Progress { get; set; }

    /// <summary>Optional human-readable progress note ("12/40 documents reviewed").</summary>
    public string? ProgressNote { get; set; }

    /// <summary>Handler output on success, serialized (shape is handler-defined).</summary>
    public string? ResultJson { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
