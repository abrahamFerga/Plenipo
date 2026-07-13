using Plenipo.Application.Usage;

namespace Plenipo.Application.Auditing;

/// <summary>
/// Writes audit records to the dedicated append-only audit store. Implementations enqueue through the
/// outbox so audit writes never block or fail the user-facing operation.
/// </summary>
public interface IAuditLog
{
    public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default);

    public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default);

    public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default);

    public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default);
}
