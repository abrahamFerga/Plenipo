using Cortex.Core.Platform;

namespace Cortex.Application.Approvals;

/// <summary>Persists and resolves <see cref="PendingApproval"/> records for the human-in-the-loop flow.</summary>
public interface IApprovalStore
{
    public Task RecordPendingAsync(PendingApproval pending, CancellationToken cancellationToken = default);

    /// <summary>Pending approvals for the current tenant, newest first.</summary>
    public Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default);

    public Task<PendingApproval?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Records the human decision, including who made it (<paramref name="resolvedByUserId"/> /
    /// <paramref name="resolvedByDisplay"/>) — the attribution the ADMT disclosure view reports.</summary>
    public Task ResolveAsync(
        Guid id, ApprovalStatus status, string? result, string? error,
        Guid? resolvedByUserId = null, string? resolvedByDisplay = null,
        CancellationToken cancellationToken = default);
}
