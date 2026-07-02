using Cortex.Application.Jobs;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// The background-job surface: poll a job's status/progress/result, list your own jobs, cancel a
/// queued one. Jobs are tenant-scoped rows; a foreign tenant's id behaves like a missing one.
/// </summary>
public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs").RequireAuthorization();

        group.MapGet("/{jobId:guid}", async (Guid jobId, IJobQueue jobs, CancellationToken cancellationToken) =>
            {
                var job = await jobs.FindAsync(jobId, cancellationToken);
                return job is null ? Results.NotFound() : Results.Ok(ToDto(job));
            })
            .WithName("Jobs_Get");

        group.MapGet("/mine", async (PlatformDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var mine = await db.BackgroundJobs
                    .Where(j => j.UserId == current.UserId)
                    .OrderByDescending(j => j.CreatedAt)
                    .Take(50)
                    .ToListAsync(cancellationToken);
                return Results.Ok(mine.Select(ToDto));
            })
            .WithName("Jobs_Mine");

        // Queued jobs cancel immediately; running ones cancel cooperatively at their next progress
        // report. Only the enqueuer can cancel their job.
        group.MapPost("/{jobId:guid}/cancel", async (Guid jobId, IJobQueue jobs, CancellationToken cancellationToken) =>
            {
                var cancelled = await jobs.TryCancelAsync(jobId, cancellationToken);
                return cancelled
                    ? Results.Ok()
                    : Results.Conflict(new { error = "The job does not exist, already finished, or is not yours to cancel." });
            })
            .WithName("Jobs_Cancel");
    }

    private static JobDto ToDto(BackgroundJob job) => new(
        job.Id, job.ModuleId, job.Kind, job.Status.ToString(), job.Progress, job.ProgressNote,
        job.ResultJson, job.Error, job.Attempts, job.CancelRequested,
        job.CreatedAt, job.StartedAt, job.CompletedAt);

    private sealed record JobDto(
        Guid Id, string ModuleId, string Kind, string Status, int Progress, string? ProgressNote,
        string? ResultJson, string? Error, int Attempts, bool CancelRequested,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
}
