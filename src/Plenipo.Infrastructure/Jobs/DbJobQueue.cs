using System.Text.Json;
using Plenipo.Application.Jobs;
using Plenipo.Core.Identity;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Jobs;

/// <summary>EF-backed <see cref="IJobQueue"/>: job rows in the platform database, tenant-scoped.</summary>
public sealed class DbJobQueue(PlatformDbContext db, ICurrentUser currentUser) : IJobQueue
{
    private static readonly JsonSerializerOptions ArgsJson = new(JsonSerializerDefaults.Web);

    public async Task<Guid> EnqueueAsync(string moduleId, string kind, object arguments, CancellationToken cancellationToken = default)
    {
        var job = new BackgroundJob
        {
            TenantId = currentUser.TenantId ?? throw new InvalidOperationException("Cannot enqueue a job without a tenant."),
            UserId = currentUser.UserId ?? throw new InvalidOperationException("Cannot enqueue a job without a user."),
            ModuleId = moduleId,
            Kind = kind,
            ArgumentsJson = JsonSerializer.Serialize(arguments, ArgsJson),
            // Capability capture: the job runs with the authority the enqueuer had right now.
            PermissionsSnapshotJson = JsonSerializer.Serialize(currentUser.Permissions, ArgsJson),
        };

        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job.Id;
    }

    public async Task<BackgroundJob?> FindAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await db.BackgroundJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    public async Task<bool> TryCancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await FindAsync(jobId, cancellationToken);
        // Only the enqueuer may cancel — a job runs under its enqueuer's authority, and tenant
        // scoping alone would let any tenant member kill a colleague's work.
        if (job is null || job.UserId != currentUser.UserId)
        {
            return false;
        }

        switch (job.Status)
        {
            case JobStatus.Queued:
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return true;

            case JobStatus.Running:
                // Cooperative: the processor observes the flag at the next progress report.
                job.CancelRequested = true;
                await db.SaveChangesAsync(cancellationToken);
                return true;

            default:
                return false;
        }
    }
}
