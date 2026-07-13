namespace Plenipo.Application.Jobs;

/// <summary>
/// Everything a handler needs while executing: the job's input, whose authority it runs under, the
/// scoped services (tenant/user context already restored — resolve DbContexts, tools, IFileStore as
/// usual), and a progress reporter the UI polls.
/// </summary>
public sealed class JobExecutionContext
{
    public required Guid JobId { get; init; }
    public required Guid TenantId { get; init; }

    /// <summary>
    /// The enqueuing user — or <c>BackgroundJob.SystemUserId</c> (<see cref="Guid.Empty"/>) for a
    /// platform-scheduled recurring run, which is tenant-scoped but has no user behind it.
    /// </summary>
    public required Guid UserId { get; init; }
    public required string ModuleId { get; init; }
    public required string ArgumentsJson { get; init; }

    /// <summary>The execution scope, with the enqueuer's tenant + user + permissions restored.</summary>
    public required IServiceProvider ScopedServices { get; init; }

    /// <summary>Persist progress (0–100) and an optional note ("12/40 documents reviewed").</summary>
    public required Func<int, string?, CancellationToken, Task> ReportProgressAsync { get; init; }
}

/// <summary>
/// Executes one kind of background job. Modules register handlers in DI
/// (<c>services.AddSingleton&lt;IJobHandler, BulkReviewJobHandler&gt;()</c>); the platform's job
/// processor dispatches queued jobs by <see cref="Kind"/>.
/// </summary>
public interface IJobHandler
{
    /// <summary>The unique kind this handler executes, conventionally "{moduleId}.{job-name}".</summary>
    public string Kind { get; }

    /// <summary>Runs the job; the return value is stored as the job's result JSON (null for none).</summary>
    public Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
}
