using Cortex.Core.Platform;

namespace Cortex.Application.Approvals;

/// <summary>Persists and resolves <see cref="PendingApproval"/> records for the human-in-the-loop flow.</summary>
public interface IApprovalStore
{
    public Task RecordPendingAsync(PendingApproval pending, CancellationToken cancellationToken = default);

    /// <summary>Pending approvals for the current tenant, newest first.</summary>
    public Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default);

    public Task<PendingApproval?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Atomically transitions one pending action to executing. Only one caller can win.</summary>
    public Task<PendingApproval?> TryBeginExecutionAsync(
        Guid id, Guid? resolvedByUserId, string? resolvedByDisplay,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically rejects a still-pending action. Returns false if another resolver won.</summary>
    public Task<bool> TryRejectAsync(
        Guid id, Guid? resolvedByUserId, string? resolvedByDisplay,
        CancellationToken cancellationToken = default);

    /// <summary>Records the human decision, including who made it (<paramref name="resolvedByUserId"/> /
    /// <paramref name="resolvedByDisplay"/>) — the attribution the ADMT disclosure view reports.</summary>
    public Task CompleteExecutionAsync(
        Guid id, ApprovalStatus status, string? result, string? error,
        CancellationToken cancellationToken = default);
}
