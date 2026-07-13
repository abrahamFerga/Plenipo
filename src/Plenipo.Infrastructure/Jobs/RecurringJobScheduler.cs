using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Application.Jobs;
using Plenipo.Application.Modules;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Jobs;

/// <summary>
/// Turns manifest-declared <c>RecurringJobs</c> into queued <see cref="BackgroundJob"/>s: every
/// sweep it walks (active tenant × declared job), asks the pure
/// <see cref="RecurringJobSchedule.IsDue"/> whether a cadence window has elapsed since the
/// tenant's <see cref="RecurringJobCursor"/> stamp, and enqueues one job per due pair — which the
/// existing <see cref="JobProcessor"/> then executes with all its lease/retry/recovery machinery.
/// Scheduling and execution stay deliberately separate: this loop only decides WHEN, so it never
/// holds a scope open for the duration of module work.
///
/// Identity: recurring runs have no enqueuing user, so jobs are enqueued under
/// <see cref="BackgroundJob.SystemUserId"/> with the module's tool wildcard
/// (<c>tools.{moduleId}.*</c>) as the permission snapshot — the run may do what the module's own
/// tools may do, nothing platform-wide. See <see cref="JobProcessor"/> for how that identity is
/// restored and audited.
///
/// Robustness: the cursor is stamped in the SAME save as the enqueued job, so a crash cannot
/// separate them (no double-fire on restart, no lost stamp), and a kind that is already Queued or
/// Running for a tenant is skipped rather than stacked — a stuck daily digest yields one late run,
/// not a backlog. Like the processor, one scheduler instance is assumed (multi-instance
/// deployments should leader-elect before scaling out; the unique (tenant, kind) cursor index
/// contains the blast radius of a race to one duplicate window at worst). Sweep precision is
/// deliberately coarse — cadences are hours to weeks, so a few minutes of slack is noise.
/// </summary>
public sealed class RecurringJobScheduler(
    IServiceScopeFactory scopeFactory,
    IModuleCatalog moduleCatalog,
    ILogger<RecurringJobScheduler> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>How often the due check runs. Minutes-coarse on purpose; tests shorten it.</summary>
    public static TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Manifests are fixed at startup, so a host with no recurring work parks the service
        // immediately — no idle polling, no per-sweep catalog walks.
        var declared = moduleCatalog.Manifests
            .SelectMany(m => m.RecurringJobs.Select(job => (ModuleId: m.Id, Job: job)))
            .ToList();
        if (declared.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(declared, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The scheduler must never die to one bad sweep; the next window simply retries
                // (an un-stamped due pair stays due — that's the catch-up contract working).
                logger.LogError(ex, "Recurring job sweep failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SweepOnceAsync(
        IReadOnlyList<(string ModuleId, Plenipo.Modules.Sdk.RecurringJobDescriptor Job)> declared,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var now = DateTimeOffset.UtcNow;

        var tenantIds = await db.Tenants
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var moduleIds = declared.Select(d => d.ModuleId).Distinct(StringComparer.Ordinal).ToList();
        var kinds = declared.Select(d => d.Job.Kind).ToList();

        // The scheduler serves every tenant, so tenant-owned reads bypass the ambient-tenant
        // filter explicitly (this scope has none). Enablement is default-on: only an explicit
        // IsEnabled=false row disables a module (mirrors TenantModuleStore on the request path).
        var disabled = (await db.TenantModules.IgnoreQueryFilters()
                .Where(tm => !tm.IsEnabled && moduleIds.Contains(tm.ModuleId))
                .Select(tm => new { tm.TenantId, tm.ModuleId })
                .ToListAsync(cancellationToken))
            .Select(x => (x.TenantId, x.ModuleId))
            .ToHashSet();

        var cursors = await db.RecurringJobCursors.IgnoreQueryFilters()
            .Where(c => kinds.Contains(c.Kind))
            .ToListAsync(cancellationToken);
        var cursorByTenantKind = cursors.ToDictionary(c => (c.TenantId, c.Kind));

        // A kind still Queued/Running for a tenant is not re-enqueued: a slow or stuck run gets
        // one late successor when it clears, never a stacked backlog.
        var inFlight = (await db.BackgroundJobs.IgnoreQueryFilters()
                .Where(j => j.UserId == BackgroundJob.SystemUserId
                    && kinds.Contains(j.Kind)
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                .Select(j => new { j.TenantId, j.Kind })
                .ToListAsync(cancellationToken))
            .Select(x => (x.TenantId, x.Kind))
            .ToHashSet();

        var enqueued = 0;
        foreach (var tenantId in tenantIds)
        {
            foreach (var (moduleId, job) in declared)
            {
                if (disabled.Contains((tenantId, moduleId)) || inFlight.Contains((tenantId, job.Kind)))
                {
                    continue;
                }

                cursorByTenantKind.TryGetValue((tenantId, job.Kind), out var cursor);
                if (!RecurringJobSchedule.IsDue(now, cursor?.LastEnqueuedAt, job.Cadence))
                {
                    continue;
                }

                db.BackgroundJobs.Add(new BackgroundJob
                {
                    TenantId = tenantId,
                    UserId = BackgroundJob.SystemUserId,
                    ModuleId = moduleId,
                    Kind = job.Kind,
                    ArgumentsJson = "{}",
                    // The system run's authority: the module's own tool surface, nothing wider.
                    PermissionsSnapshotJson = JsonSerializer.Serialize(
                        new[] { Permissions.AllToolsFor(moduleId) }, SnapshotJson),
                });

                if (cursor is null)
                {
                    db.RecurringJobCursors.Add(new RecurringJobCursor
                    {
                        TenantId = tenantId,
                        Kind = job.Kind,
                        LastEnqueuedAt = now,
                    });
                }
                else
                {
                    cursor.LastEnqueuedAt = now;
                }

                enqueued++;
            }
        }

        if (enqueued > 0)
        {
            // One save covers every job + cursor pair, so a crash mid-sweep loses both halves of
            // a pair together — the next sweep re-fires it exactly once.
            await db.SaveChangesAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Recurring sweep enqueued {Count} job(s).", enqueued);
            }
        }
    }
}
