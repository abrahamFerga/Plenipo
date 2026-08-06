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

    /// <summary>
    /// Records one completed-or-failed agent turn. Called from the runner's <c>finally</c>, so
    /// implementations must tolerate being invoked while the turn's own token is already cancelled.
    /// </summary>
    public Task RecordAgentRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default);
}
