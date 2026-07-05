using Cortex.Application.Notifications;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Modules.Legal;

/// <summary>
/// The docketing reminder: periodically scans open, un-reminded deadlines whose reminder window
/// has opened and produces one notification each to the user who docketed them (persisted inbox
/// first, then any configured channels — the platform notifier's contract). Runs OUTSIDE any
/// request scope, so it ignores tenant query filters and carries tenant/user explicitly on each
/// notification. One-shot per deadline: <see cref="MatterDeadline.ReminderSentAt"/> is the latch.
/// </summary>
public sealed class DeadlineReminderService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeadlineReminderService> logger) : BackgroundService
{
    /// <summary>Scan cadence — reminders are day-granular, so minutes of drift are irrelevant.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Widest supported reminder window; the SQL prefilter uses it, the exact per-row check runs in memory.</summary>
    public const int MaxReminderDaysBefore = 90;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations/seeding finish before the first scan.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sent = await ScanOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow, stoppingToken);
                if (sent > 0 && logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Deadline scan produced {Count} reminder(s).", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed scan must never kill the service — the next tick retries everything
                // still un-latched.
                logger.LogWarning(ex, "Deadline reminder scan failed; will retry next interval.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>
    /// One scan pass, factored for tests: finds due reminders across ALL tenants, notifies, and
    /// latches each. Returns how many reminders were produced.
    /// </summary>
    public static async Task<int> ScanOnceAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<LegalDbContext>();
        var notifier = services.GetRequiredService<INotifier>();

        // Cross-tenant by design (no ambient tenant here): SQL prefilters to the widest window,
        // the entity's own IsReminderDue applies each row's exact ReminderDaysBefore.
        var horizon = now.AddDays(MaxReminderDaysBefore);
        var candidates = await db.MatterDeadlines
            .IgnoreQueryFilters()
            .Where(d => d.CompletedAt == null && (d.ReminderSentAt == null || d.FinalNoticeSentAt == null) && d.DueAt <= horizon)
            // A CLOSED matter's dates never remind — closing (with its completeness check) is the
            // explicit decision that this file's obligations are done.
            .Join(db.Matters.IgnoreQueryFilters().Where(m => m.Status == MatterStatus.Open),
                d => d.MatterId, m => m.Id, (d, m) => new { Deadline = d, MatterName = m.Name })
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var row in candidates)
        {
            var deadline = row.Deadline;
            if (deadline.OwnerUserId is not Guid owner)
            {
                continue;
            }

            var days = (int)Math.Ceiling((deadline.DueAt - now).TotalDays);

            // Stage 2 — the due-day final notice. It SUPERSEDES a pending early reminder (one
            // urgent notification, not two in the same scan) by latching both stamps.
            if (deadline.IsFinalNoticeDue(now))
            {
                var overdue = days < 0 ? $"is OVERDUE by {-days} day(s)" : "is DUE TODAY";
                await notifier.NotifyAsync(new Notification(
                    deadline.TenantId,
                    owner,
                    Category: "legal.deadline",
                    Title: $"DEADLINE DUE: {deadline.Title}",
                    Body: $"'{deadline.Title}' on matter '{row.MatterName}' {overdue} ({deadline.DueAt:yyyy-MM-dd}). Act now or mark it completed.",
                    Link: "/legal/deadlines"), cancellationToken);

                deadline.FinalNoticeSentAt = now;
                deadline.ReminderSentAt ??= now;
                sent++;
                continue;
            }

            // Stage 1 — the early heads-up when the reminder window opens.
            if (deadline.IsReminderDue(now))
            {
                var when = days == 0 ? "is due today" : $"is due in {days} day(s)";
                await notifier.NotifyAsync(new Notification(
                    deadline.TenantId,
                    owner,
                    Category: "legal.deadline",
                    Title: $"Deadline: {deadline.Title}",
                    Body: $"'{deadline.Title}' on matter '{row.MatterName}' {when} ({deadline.DueAt:yyyy-MM-dd}).",
                    Link: "/legal/deadlines"), cancellationToken);

                deadline.ReminderSentAt = now;
                sent++;
            }
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }
}
