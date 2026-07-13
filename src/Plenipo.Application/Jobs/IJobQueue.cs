using Plenipo.Core.Platform;

namespace Plenipo.Application.Jobs;

/// <summary>
/// Enqueues background jobs under the current caller's tenant and user — the platform's primitive
/// for work that outlives a request (bulk document review, batch imports). Handlers are registered
/// per kind via <see cref="IJobHandler"/>; the hosted processor executes them with the enqueuer's
/// identity restored, so RBAC, tenant filters, and audit hold inside jobs.
/// </summary>
public interface IJobQueue
{
    /// <summary>Enqueues a job and returns its id (pollable at /api/jobs/{id}).</summary>
    public Task<Guid> EnqueueAsync(string moduleId, string kind, object arguments, CancellationToken cancellationToken = default);

    /// <summary>The job row, tenant-scoped. Null when the id doesn't exist in this tenant.</summary>
    public Task<BackgroundJob?> FindAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the caller's own job: a Queued job is cancelled immediately; a Running one gets a
    /// cancellation request the processor honors at the job's next progress report. Returns false
    /// when the job is already finished, belongs to someone else, or doesn't exist.
    /// </summary>
    public Task<bool> TryCancelAsync(Guid jobId, CancellationToken cancellationToken = default);
}
