using Cortex.Application.Jobs;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure.Jobs;

/// <summary>
/// Executes queued <see cref="BackgroundJob"/>s. For each job it builds a fresh scope and restores
/// the enqueuer's identity the same way the request pipeline would — tenant first (query filters),
/// then user, then permissions resolved from the user's DB roles/grants — so everything a handler
/// touches behaves exactly as it does in a chat turn. Jobs run one at a time per instance
/// (deliberate: bulk work is throughput-insensitive and this keeps claiming trivially correct;
/// multi-instance deployments should add SKIP LOCKED claiming before scaling out workers).
///
/// Robustness: each claim takes a <see cref="LeaseDuration"/> lease, extended at every progress
/// report. A Running job whose lease expired was orphaned by a crashed host — the poll loop
/// requeues it while attempts remain (<see cref="MaxAttempts"/>), then fails it. A graceful
/// shutdown requeues the in-flight job the same way. Handler exceptions are NOT retried: they are
/// treated as deterministic and fail the job immediately, so a poisoned document can't burn three
/// full runs.
/// </summary>
public sealed class JobProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<JobProcessor> logger) : BackgroundService
{
    /// <summary>Poll cadence for new work. Short enough for UI polling to feel live.</summary>
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a claim stays valid without a progress report. Handlers that can outlive this
    /// between two reports should report more often — progress is also the liveness signal.
    /// </summary>
    public static TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Total executions a job may consume (first run + reruns after lost leases).</summary>
    public static int MaxAttempts { get; set; } = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ranOne = await RunNextQueuedJobAsync(stoppingToken);
                if (!ranOne)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The processor itself must never die to one bad job/claim; the job's own failure
                // handling below records per-job errors.
                logger.LogError(ex, "Job processor loop error; continuing.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task<bool> RunNextQueuedJobAsync(CancellationToken cancellationToken)
    {
        using var claimScope = scopeFactory.CreateScope();
        var claimDb = claimScope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await RecoverExpiredLeasesAsync(claimDb, cancellationToken);

        // Claim the oldest queued job (cross-tenant: the processor serves every tenant).
        var job = await claimDb.BackgroundJobs.IgnoreQueryFilters()
            .Where(j => j.Status == JobStatus.Queued)
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return false;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Attempts++;
        job.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration;
        await claimDb.SaveChangesAsync(cancellationToken);

        await ExecuteJobAsync(job.Id, cancellationToken);
        return true;
    }

    /// <summary>
    /// Requeues Running jobs whose lease expired (a crashed/stopped host never got to update them)
    /// while they have attempts left; fails them when they don't. Runs on every poll, so recovery
    /// needs no dedicated timer and works even when this is the instance that crashed.
    /// </summary>
    private async Task RecoverExpiredLeasesAsync(PlatformDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var orphaned = await db.BackgroundJobs.IgnoreQueryFilters()
            .Where(j => j.Status == JobStatus.Running && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);
        if (orphaned.Count == 0)
        {
            return;
        }

        foreach (var job in orphaned)
        {
            if (job.Attempts >= MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.Error = $"The job's lease expired mid-run and all {job.Attempts} attempt(s) are used up (host crash or overload).";
                job.CompletedAt = now;
                job.LeaseExpiresAt = null;
                logger.LogError("Background job {JobId} ({Kind}) failed after {Attempts} lost lease(s).", job.Id, job.Kind, job.Attempts);
            }
            else
            {
                job.Status = JobStatus.Queued;
                job.ProgressNote = "requeued after a lost lease (the previous host stopped mid-run)";
                job.LeaseExpiresAt = null;
                logger.LogWarning("Background job {JobId} ({Kind}) lease expired; requeued (attempt {Attempts}).", job.Id, job.Kind, job.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ExecuteJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<PlatformDbContext>();

        var job = await db.BackgroundJobs.IgnoreQueryFilters().FirstAsync(j => j.Id == jobId, cancellationToken);

        try
        {
            // Restore the enqueuer's identity in this scope (the WhatsApp channel does the same).
            var context = services.GetRequiredService<RequestContext>();
            context.SetTenant(job.TenantId);

            var user = await db.Users.IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Id == job.UserId, cancellationToken)
                ?? throw new InvalidOperationException($"Job {job.Id}: enqueuing user {job.UserId} no longer exists.");
            if (!user.IsActive)
            {
                throw new InvalidOperationException($"Job {job.Id}: enqueuing user is deactivated.");
            }

            context.SetUser(user.Id, user.Subject, user.DisplayName);

            // Restore the authority captured at enqueue time. Token-asserted roles have no DB rows
            // (deliberately — see RequestEnricher), so re-resolving here would under-authorize; the
            // snapshot is both sufficient and the honest audit record of the job's allowed powers.
            var permissions = System.Text.Json.JsonSerializer.Deserialize<string[]>(job.PermissionsSnapshotJson) ?? [];
            context.SetPermissions(permissions);

            var handler = services.GetServices<IJobHandler>()
                .FirstOrDefault(h => string.Equals(h.Kind, job.Kind, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"No job handler is registered for kind '{job.Kind}'.");

            var execution = new JobExecutionContext
            {
                JobId = job.Id,
                TenantId = job.TenantId,
                UserId = job.UserId,
                ModuleId = job.ModuleId,
                ArgumentsJson = job.ArgumentsJson,
                ScopedServices = services,
                ReportProgressAsync = async (percent, note, ct) =>
                {
                    // Progress reports double as the cancellation point and the liveness signal.
                    // Read the flag fresh — it is set from another scope's context.
                    var cancelRequested = await db.BackgroundJobs.IgnoreQueryFilters().AsNoTracking()
                        .Where(j => j.Id == job.Id)
                        .Select(j => j.CancelRequested)
                        .FirstOrDefaultAsync(ct);
                    if (cancelRequested)
                    {
                        throw new OperationCanceledException($"Job {job.Id} was cancelled by the user.");
                    }

                    job.Progress = Math.Clamp(percent, 0, 100);
                    job.ProgressNote = note;
                    job.LeaseExpiresAt = DateTimeOffset.UtcNow + LeaseDuration;
                    await db.SaveChangesAsync(ct);
                },
            };

            job.ResultJson = await handler.ExecuteAsync(execution, cancellationToken);
            job.Status = JobStatus.Succeeded;
            job.Progress = 100;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown mid-run — not the job's fault. The claim already counted this attempt;
            // requeue for the next start while attempts remain.
            if (job.Attempts >= MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.Error = $"The host shut down while the job was running and all {job.Attempts} attempt(s) are used up.";
            }
            else
            {
                job.Status = JobStatus.Queued;
                job.ProgressNote = "requeued: the host shut down mid-run";
            }
        }
        catch (OperationCanceledException)
        {
            // The enqueuer asked for cancellation; the progress callback observed the flag.
            job.Status = JobStatus.Cancelled;
            job.ProgressNote = "cancelled while running";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background job {JobId} ({Kind}) failed.", job.Id, job.Kind);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.LeaseExpiresAt = null;
            if (job.Status is not JobStatus.Queued)
            {
                job.CompletedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
