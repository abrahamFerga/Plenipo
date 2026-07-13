using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Auditing;

/// <summary>
/// Writes audit records to the dedicated audit database, with a durable fallback so a transient audit-DB
/// outage cannot drop a record. The happy path is a direct, synchronous write (immediately queryable). If
/// that throws, the record is serialized to a durable outbox in the platform database (which is up — it's
/// the operational DB), where the <see cref="AuditOutboxProcessor"/> flushes it once the audit store
/// recovers. Auditing still never breaks the user-facing operation.
/// </summary>
public sealed class AuditLog(AuditDbContext db, OutboxDbContext outbox, ILogger<AuditLog> logger) : IAuditLog
{
    public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) =>
        WriteAsync(() => db.ToolCalls.Add(entry), () => AuditOutboxSerializer.ForToolCall(entry), cancellationToken);

    public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default) =>
        WriteAsync(() => db.AuthEvents.Add(entry), () => AuditOutboxSerializer.ForAuthEvent(entry), cancellationToken);

    public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(() => db.EntityChanges.AddRange(entries), () => AuditOutboxSerializer.ForEntityChanges(entries), cancellationToken);
    }

    public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) =>
        WriteAsync(() => db.TokenUsage.Add(record), () => AuditOutboxSerializer.ForTokenUsage(record), cancellationToken);

    private async Task WriteAsync(Action stage, Func<AuditOutboxMessage> toOutbox, CancellationToken cancellationToken)
    {
        try
        {
            stage();
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        catch (Exception ex)
        {
            // Detach the failed add so it isn't retried (and possibly duplicated) on the next audit write
            // through this scoped context — the durable outbox owns this record from here.
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Direct audit write failed; deferring the record to the durable outbox.");
        }

        try
        {
            outbox.Messages.Add(toOutbox());
            await outbox.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Durable audit outbox enqueue also failed; the audit record was lost.");
        }
    }
}
