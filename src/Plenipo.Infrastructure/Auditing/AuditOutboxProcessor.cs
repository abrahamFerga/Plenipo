using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Auditing;

/// <summary>
/// Drains the durable audit outbox into the audit store on a schedule. Each pending message is flushed
/// independently — a single poison message increments its attempt count and is left for retry rather than
/// blocking the rest. On the happy path the outbox is empty (audit writes go straight through), so this is
/// a cheap no-op; it only does real work after an audit-DB outage deferred some records.
/// </summary>
public sealed class AuditOutboxProcessor(IServiceScopeFactory scopeFactory, ILogger<AuditOutboxProcessor> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit outbox drain failed; will retry on the next tick.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();

        var pending = await outbox.Messages
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        foreach (var message in pending)
        {
            try
            {
                AuditOutboxSerializer.Apply(message, audit);
                await audit.SaveChangesAsync(cancellationToken);

                outbox.Messages.Remove(message);
                await outbox.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                audit.ChangeTracker.Clear();
                message.Attempts++;
                await outbox.SaveChangesAsync(cancellationToken);
                logger.LogWarning(ex, "Failed to flush audit outbox message {MessageId} (attempt {Attempts}); leaving it for retry.",
                    message.Id, message.Attempts);
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
