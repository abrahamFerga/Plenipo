using System.Text.Json;
using Plenipo.Application.Jobs;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Jobs;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Module-declared recurring jobs end to end: the scheduler turns a manifest
/// <c>RecurringJobs</c> declaration into a queued job per enabled tenant, the processor executes
/// it under the tenant-scoped SYSTEM identity (no user, the module's tool wildcard — the property
/// the whole system-execution path exists for), the per-tenant cursor prevents a second fire
/// inside the cadence window, and a tenant that disabled the module is never scheduled.
/// </summary>
public sealed class RecurringJobTests : IAsyncLifetime
{
    private PlenipoApiFactory _factory = default!;

    public async Task InitializeAsync()
    {
        JobProcessor.PollInterval = TimeSpan.FromMilliseconds(50);
        RecurringJobScheduler.SweepInterval = TimeSpan.FromMilliseconds(100);
        _factory = new RecurringJobFactory();
        using var warmup = _factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed class RecurringJobFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IModule>(new RecurringTestModule());
                services.AddSingleton<IJobHandler, TickJobHandler>();
            });
        }
    }

    /// <summary>A module whose only feature is a daily recurring job — the digest shape.</summary>
    private sealed class RecurringTestModule : IModule
    {
        public ModuleManifest Manifest { get; } = new()
        {
            Id = "rectest",
            DisplayName = "Recurring Test Module",
            Version = "1.0.0",
            RecurringJobs =
            [
                new RecurringJobDescriptor("rectest.tick", RecurringJobCadence.Daily, "Ticks once a day."),
            ],
        };

        public void RegisterServices(IServiceCollection services, IConfiguration configuration)
        {
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
        }
    }

    /// <summary>Asserts the system identity the platform promises recurring runs, then echoes it.</summary>
    private sealed class TickJobHandler : IJobHandler
    {
        public string Kind => "rectest.tick";

        public Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            var current = context.ScopedServices.GetRequiredService<Plenipo.Core.Identity.ICurrentUser>();

            // Tenant-scoped: query filters and module data access must work as in any job…
            if (current.TenantId != context.TenantId)
            {
                throw new InvalidOperationException("System job scope does not carry the tenant.");
            }

            // …but there is NO user behind the run: audit shows the scheduler, not a person.
            if (current.UserId is not null || context.UserId != BackgroundJob.SystemUserId)
            {
                throw new InvalidOperationException("System job scope must not carry a user identity.");
            }

            // The promised authority: the module's own tool surface, nothing platform-wide.
            if (!current.HasPermission("tools.rectest.anything") || current.HasPermission("platform.users.manage"))
            {
                throw new InvalidOperationException("System job scope has the wrong permission snapshot.");
            }

            return Task.FromResult<string?>(JsonSerializer.Serialize(new { ticked = true }));
        }
    }

    [Fact]
    public async Task Declared_recurring_job_runs_once_per_window_under_the_system_identity()
    {
        // The sweep fires the first run immediately (no cursor yet); wait for it to succeed.
        var job = await PollForTerminalSystemJobAsync("rectest.tick");
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(BackgroundJob.SystemUserId, job.UserId);
        Assert.Contains("ticked", job.ResultJson);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            // The cursor row is the restart-safety contract: stamped with the enqueue.
            var cursor = await db.RecurringJobCursors.IgnoreQueryFilters()
                .SingleAsync(c => c.Kind == "rectest.tick" && c.TenantId == job.TenantId);
            Assert.True(cursor.LastEnqueuedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        }

        // Several sweep intervals later the daily window has NOT elapsed — still exactly one job.
        await Task.Delay(500);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var count = await db.BackgroundJobs.IgnoreQueryFilters()
                .CountAsync(j => j.Kind == "rectest.tick" && j.TenantId == job.TenantId);
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task Tenant_that_disabled_the_module_is_never_scheduled()
    {
        // Stage the disable row BEFORE the tenant becomes visible to the sweep, so there is no
        // instant where "other" exists with the module still enabled.
        Guid otherTenantId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            otherTenantId = Guid.CreateVersion7();
            db.TenantModules.Add(new TenantModule
            {
                TenantId = otherTenantId,
                ModuleId = "rectest",
                IsEnabled = false,
            });
            await db.SaveChangesAsync();

            db.Tenants.Add(new Tenant { Id = otherTenantId, Name = "Other", Slug = "other-rec" });
            await db.SaveChangesAsync();
        }

        // The dev tenant's run proves sweeps are happening; then give the sweep several more
        // passes over the now-visible "other" tenant.
        var devJob = await PollForTerminalSystemJobAsync("rectest.tick");
        Assert.Equal(JobStatus.Succeeded, devJob.Status);
        await Task.Delay(500);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var forOther = await db.BackgroundJobs.IgnoreQueryFilters()
                .AnyAsync(j => j.Kind == "rectest.tick" && j.TenantId == otherTenantId);
            Assert.False(forOther, "a disabled module was scheduled for its tenant");

            // And no cursor either — the pair was skipped before the due check, not after.
            var cursorForOther = await db.RecurringJobCursors.IgnoreQueryFilters()
                .AnyAsync(c => c.Kind == "rectest.tick" && c.TenantId == otherTenantId);
            Assert.False(cursorForOther);
        }
    }

    private async Task<BackgroundJob> PollForTerminalSystemJobAsync(string kind)
    {
        for (var i = 0; i < 150; i++)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var job = await db.BackgroundJobs.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(j => j.Kind == kind && j.Status != JobStatus.Queued && j.Status != JobStatus.Running);
            if (job is not null)
            {
                return job;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No terminal '{kind}' job appeared.");
    }
}
