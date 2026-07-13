using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Jobs;
using Plenipo.Infrastructure.Jobs;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The background-job primitive end to end: enqueue under a user's identity, the processor restores
/// tenant + user + permissions and executes the registered handler, progress and result are pollable
/// over the API, and tenant isolation holds. The echo handler also asserts the restored identity —
/// the property the whole primitive exists for.
/// </summary>
public sealed class BackgroundJobTests : IAsyncLifetime
{
    private PlenipoApiFactory _factory = default!;

    public async Task InitializeAsync()
    {
        JobProcessor.PollInterval = TimeSpan.FromMilliseconds(50); // keep test polling snappy
        _factory = new EchoJobFactory();
        using var warmup = _factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private sealed class EchoJobFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IJobHandler, EchoJobHandler>();
                services.AddSingleton<IJobHandler, SlowJobHandler>();
            });
        }
    }

    /// <summary>Reports progress, verifies the restored identity, and echoes its arguments.</summary>
    private sealed class EchoJobHandler : IJobHandler
    {
        public string Kind => "test.echo";

        public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            // The scope must carry the enqueuer's identity — tenant filters and RBAC depend on it.
            var current = context.ScopedServices.GetRequiredService<Plenipo.Core.Identity.ICurrentUser>();
            if (current.TenantId != context.TenantId || current.UserId != context.UserId)
            {
                throw new InvalidOperationException("Job scope does not carry the enqueuer's identity.");
            }

            if (!current.HasPermission("chat.use"))
            {
                throw new InvalidOperationException("Job scope did not resolve the user's permissions.");
            }

            await context.ReportProgressAsync(50, "halfway", cancellationToken);
            var args = JsonDocument.Parse(context.ArgumentsJson).RootElement;
            return JsonSerializer.Serialize(new { echoed = args.GetProperty("message").GetString() });
        }
    }

    /// <summary>Runs for seconds, reporting progress every step — the cooperative-cancel target.</summary>
    private sealed class SlowJobHandler : IJobHandler
    {
        public string Kind => "test.slow";

        public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            for (var i = 1; i <= 200; i++)
            {
                // Each report is a cancellation point; a requested cancel throws from inside it.
                await context.ReportProgressAsync(Math.Min(99, i), $"step {i}", cancellationToken);
                await Task.Delay(25, cancellationToken);
            }

            return null;
        }
    }

    private HttpClient ClientFor(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    [Fact]
    public async Task Enqueued_job_executes_under_the_enqueuers_identity_and_is_pollable()
    {
        using var client = ClientFor("user", "job-user");
        (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode(); // JIT-provision

        // Enqueue from a scope carrying that user (module code would do this inside a tool/endpoint).
        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
            context.SetTenant(tenant.Id);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "job-user");
            context.SetUser(user.Id, user.Subject, user.DisplayName);
            // In a real request the enricher resolves these; the snapshot captures them for the job.
            context.SetPermissions(["chat.use", "tools.test.*"]);

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            jobId = await queue.EnqueueAsync("test", "test.echo", new { message = "bulk review me" });
        }

        // Poll the public API until the processor completes it.
        JsonElement job = default;
        for (var i = 0; i < 100; i++)
        {
            job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
            if (job.GetProperty("status").GetString() is "Succeeded" or "Failed")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(
            job.GetProperty("status").GetString() == "Succeeded",
            $"job did not succeed: status={job.GetProperty("status")}, error={job.GetProperty("error")}");
        Assert.Equal(100, job.GetProperty("progress").GetInt32());
        Assert.Contains("bulk review me", job.GetProperty("resultJson").GetString());

        // The owner sees it in their list.
        var mine = await client.GetFromJsonAsync<JsonElement>("/api/jobs/mine");
        Assert.Contains(mine.EnumerateArray(), j => j.GetProperty("id").GetGuid() == jobId);
    }

    [Fact]
    public async Task Jobs_are_tenant_scoped_and_unknown_kinds_fail_cleanly()
    {
        using var client = ClientFor("user", "job-user-2");
        (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        Guid unknownKindJob;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
            context.SetTenant(tenant.Id);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "job-user-2");
            context.SetUser(user.Id, user.Subject, user.DisplayName);
            context.SetPermissions(["chat.use"]);

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            unknownKindJob = await queue.EnqueueAsync("test", "test.no-such-handler", new { });
        }

        // An unregistered kind fails the job (with the reason recorded), never the processor.
        JsonElement job = default;
        for (var i = 0; i < 100; i++)
        {
            job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{unknownKindJob}");
            if (job.GetProperty("status").GetString() is "Succeeded" or "Failed")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("no-such-handler", job.GetProperty("error").GetString());

        // A caller from another tenant cannot see the job at all.
        using var foreign = _factory.CreateClient();
        foreign.DefaultRequestHeaders.Add("X-Dev-Subject", "foreign-admin");
        foreign.DefaultRequestHeaders.Add("X-Dev-Tenant", "other-tenant");
        foreign.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        // (other-tenant doesn't exist in the seeded store; enrichment yields no tenant → filters hide everything)
        var response = await foreign.GetAsync($"/api/jobs/{unknownKindJob}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Orphaned_running_job_is_requeued_and_completes_on_the_next_attempt()
    {
        using var client = ClientFor("user", "lease-user");
        (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        // Simulate a host that crashed mid-run: a Running row whose lease has already expired.
        var jobId = await InsertRunningJobWithExpiredLeaseAsync("lease-user", attempts: 1);

        var job = await PollUntilTerminalAsync(client, jobId);
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(2, job.GetProperty("attempts").GetInt32()); // the crashed run + the recovery run

        var note = job.GetProperty("progressNote").GetString();
        Assert.False(string.IsNullOrEmpty(note)); // the handler's own progress overwrote the requeue note
    }

    [Fact]
    public async Task Orphaned_running_job_fails_permanently_once_attempts_are_exhausted()
    {
        using var client = ClientFor("user", "lease-user-2");
        (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        var jobId = await InsertRunningJobWithExpiredLeaseAsync("lease-user-2", attempts: JobProcessor.MaxAttempts);

        var job = await PollUntilTerminalAsync(client, jobId);
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("lease expired", job.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Running_job_cancels_cooperatively_and_only_for_its_owner()
    {
        using var owner = ClientFor("user", "cancel-user");
        (await owner.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        Guid jobId;
        using (var scope = _factory.Services.CreateScope())
        {
            var queue = await ScopedQueueAsAsync(scope, "cancel-user");
            jobId = await queue.EnqueueAsync("test", "test.slow", new { });
        }

        // Wait until the processor has actually started it (progress > 0 ⇒ inside the loop).
        for (var i = 0; i < 100; i++)
        {
            var running = await owner.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
            if (running.GetProperty("status").GetString() == "Running" && running.GetProperty("progress").GetInt32() > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        // A same-tenant NON-owner cannot cancel it (a job runs under its enqueuer's authority).
        using var other = ClientFor("user", "cancel-bystander");
        (await other.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        var denied = await other.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        // The owner can; the handler observes the flag at its next progress report.
        var accepted = await owner.PostAsync($"/api/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var job = await PollUntilTerminalAsync(owner, jobId);
        Assert.Equal("Cancelled", job.GetProperty("status").GetString());
        Assert.Equal("cancelled while running", job.GetProperty("progressNote").GetString());
    }

    /// <summary>Sets the scope's identity to <paramref name="subject"/> and returns its job queue.</summary>
    private static async Task<IJobQueue> ScopedQueueAsAsync(IServiceScope scope, string subject)
    {
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["chat.use"]);
        return scope.ServiceProvider.GetRequiredService<IJobQueue>();
    }

    /// <summary>A Running echo job whose lease is already in the past — what a host crash leaves behind.</summary>
    private async Task<Guid> InsertRunningJobWithExpiredLeaseAsync(string subject, int attempts)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);

        var job = new Plenipo.Core.Platform.BackgroundJob
        {
            TenantId = tenant.Id,
            UserId = user.Id,
            ModuleId = "test",
            Kind = "test.echo",
            ArgumentsJson = """{"message":"survived the crash"}""",
            PermissionsSnapshotJson = """["chat.use"]""",
            Status = Plenipo.Core.Platform.JobStatus.Running,
            Attempts = attempts,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        };
        db.BackgroundJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static async Task<JsonElement> PollUntilTerminalAsync(HttpClient client, Guid jobId)
    {
        JsonElement job = default;
        for (var i = 0; i < 150; i++)
        {
            job = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}");
            if (job.GetProperty("status").GetString() is "Succeeded" or "Failed" or "Cancelled")
            {
                return job;
            }

            await Task.Delay(100);
        }

        return job;
    }
}
