using Plenipo.Core.Platform;

namespace Plenipo.Application.Approvals;

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

    /// <summary>
    /// This conversation's resolved approvals (executed, failed, or rejected) whose outcome has not
    /// yet been reported back into the conversation, oldest resolution first. The runner prepends
    /// these to the next turn's model input and then calls <see cref="MarkSurfacedAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<PendingApproval>> ListResolvedUnsurfacedAsync(
        Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Marks outcomes as reported to the model, so a later turn does not repeat them.</summary>
    public Task MarkSurfacedAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default);
}
