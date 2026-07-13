using Plenipo.Application.Ai;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Ai;

/// <summary>
/// Insert-if-missing snapshot persistence. Provenance is best-effort by design: a failure to
/// record a snapshot must never fail the user's chat turn — the unique index makes concurrent
/// first-writers benign, and anything else is logged and swallowed.
/// </summary>
public sealed class InstructionSnapshotStore(
    PlatformDbContext db,
    ITenantContext tenant,
    ILogger<InstructionSnapshotStore> logger) : IInstructionSnapshotStore
{
    public async Task EnsureAsync(string hash, string instructions, CancellationToken cancellationToken = default)
    {
        InstructionSnapshot? snapshot = null;
        try
        {
            if (await db.InstructionSnapshots.AnyAsync(s => s.Hash == hash, cancellationToken))
            {
                return;
            }

            snapshot = new InstructionSnapshot
            {
                TenantId = tenant.RequireTenantId(),
                Hash = hash,
                Instructions = instructions,
            };
            db.InstructionSnapshots.Add(snapshot);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Detach the failed row so the shared scoped context doesn't retry it on the turn's
            // later SaveChanges — provenance failing must never fail the chat turn itself.
            if (snapshot is not null)
            {
                db.Entry(snapshot).State = EntityState.Detached;
            }

            logger.LogWarning(ex, "Could not record instruction snapshot (provenance is best-effort)");
        }
    }
}
