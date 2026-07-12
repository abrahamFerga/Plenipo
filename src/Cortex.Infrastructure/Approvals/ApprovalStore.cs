using Cortex.Application.Approvals;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Infrastructure.Approvals;

/// <summary>
/// Stores pending approvals in the platform database. As a tenant-owned entity, <see cref="PendingApproval"/>
/// is automatically scoped by the global query filter, so a tenant only ever sees its own approvals.
/// </summary>
public sealed class ApprovalStore(PlatformDbContext db) : IApprovalStore
{
    public async Task RecordPendingAsync(PendingApproval pending, CancellationToken cancellationToken = default)
    {
        db.PendingApprovals.Add(pending);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingApproval>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await db.PendingApprovals
            .Where(p => p.Status == ApprovalStatus.Pending)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<PendingApproval?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.PendingApprovals.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task ResolveAsync(
        Guid id, ApprovalStatus status, string? result, string? error,
        Guid? resolvedByUserId = null, string? resolvedByDisplay = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await db.PendingApprovals.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (pending is null)
        {
            return;
        }

        pending.Status = status;
        pending.Result = result;
        pending.Error = error;
        pending.ResolvedAt = DateTimeOffset.UtcNow;
        pending.ResolvedByUserId = resolvedByUserId;
        pending.ResolvedByDisplay = resolvedByDisplay;
        await db.SaveChangesAsync(cancellationToken);
    }
}
