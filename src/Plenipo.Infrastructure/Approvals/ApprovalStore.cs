using Plenipo.Application.Approvals;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Approvals;

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

    public async Task<PendingApproval?> TryBeginExecutionAsync(
        Guid id, Guid? resolvedByUserId, string? resolvedByDisplay,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.PendingApprovals.FirstOrDefaultAsync(
                p => p.Id == id && p.Status == ApprovalStatus.Pending, cancellationToken);
            if (tracked is null)
            {
                return null;
            }

            MarkExecuting(tracked, resolvedByUserId, resolvedByDisplay);
            await db.SaveChangesAsync(cancellationToken);
            return tracked;
        }

        var now = DateTimeOffset.UtcNow;
        var changed = await db.PendingApprovals
            .Where(p => p.Id == id && p.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, ApprovalStatus.Executing)
                .SetProperty(p => p.ResolvedAt, now)
                .SetProperty(p => p.ResolvedByUserId, resolvedByUserId)
                .SetProperty(p => p.ResolvedByDisplay, resolvedByDisplay), cancellationToken);

        return changed == 1
            ? await db.PendingApprovals.AsNoTracking().FirstAsync(p => p.Id == id, cancellationToken)
            : null;
    }

    public async Task<bool> TryRejectAsync(
        Guid id, Guid? resolvedByUserId, string? resolvedByDisplay,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
        {
            var tracked = await db.PendingApprovals.FirstOrDefaultAsync(
                p => p.Id == id && p.Status == ApprovalStatus.Pending, cancellationToken);
            if (tracked is null)
            {
                return false;
            }

            MarkResolved(tracked, ApprovalStatus.Rejected, null, null, resolvedByUserId, resolvedByDisplay);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        return await db.PendingApprovals
            .Where(p => p.Id == id && p.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, ApprovalStatus.Rejected)
                .SetProperty(p => p.ResolvedAt, now)
                .SetProperty(p => p.ResolvedByUserId, resolvedByUserId)
                .SetProperty(p => p.ResolvedByDisplay, resolvedByDisplay), cancellationToken) == 1;
    }

    public async Task CompleteExecutionAsync(
        Guid id, ApprovalStatus status, string? result, string? error,
        CancellationToken cancellationToken = default)
    {
        if (status is not (ApprovalStatus.Executed or ApprovalStatus.Failed))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Execution may complete only as Executed or Failed.");
        }

        var executing = await db.PendingApprovals.FirstOrDefaultAsync(
            p => p.Id == id && p.Status == ApprovalStatus.Executing, cancellationToken);
        if (executing is null)
        {
            return;
        }

        executing.Status = status;
        executing.Result = result;
        executing.Error = error;
        executing.ResolvedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void MarkExecuting(PendingApproval approval, Guid? userId, string? display)
    {
        approval.Status = ApprovalStatus.Executing;
        approval.ResolvedAt = DateTimeOffset.UtcNow;
        approval.ResolvedByUserId = userId;
        approval.ResolvedByDisplay = display;
    }

    private static void MarkResolved(
        PendingApproval approval, ApprovalStatus status, string? result, string? error,
        Guid? userId, string? display)
    {
        approval.Status = status;
        approval.Result = result;
        approval.Error = error;
        approval.ResolvedAt = DateTimeOffset.UtcNow;
        approval.ResolvedByUserId = userId;
        approval.ResolvedByDisplay = display;
    }
}
