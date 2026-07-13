using Plenipo.Application.Connectors;
using Plenipo.Application.Files;
using Plenipo.Application.Rag;
using Plenipo.Core.Identity;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Legal;

/// <summary>
/// What happens to files a connector sync imports for a matter: attach them as matter documents
/// and — when the knowledge pipeline is on — index them straight into the matter's collection, so
/// "keep this matter in sync with our SharePoint/folder" ends in cited, searchable knowledge with
/// no extra steps. Runs inside the sync job, under the enqueuer's captured authority; the ethical
/// wall is re-checked (fail closed).
/// </summary>
public sealed class MatterSyncHandler(
    LegalDbContext db,
    IFileStore files,
    ICurrentUser currentUser,
    IRagService? rag = null) : IConnectorSyncHandler
{
    public string ResourceType => MatterRagGate.MatterResourceType;

    public async Task OnFilesSyncedAsync(Guid resourceId, IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken = default)
    {
        var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == resourceId, cancellationToken);
        if (matter is null || !matter.IsAccessibleTo(currentUser.UserId))
        {
            throw new InvalidOperationException("The bound matter no longer exists or is behind an ethical wall.");
        }

        foreach (var fileId in fileIds)
        {
            var file = await files.FindAsync(fileId, cancellationToken);
            if (file is null)
            {
                continue;
            }

            var attached = await db.MatterDocuments
                .AnyAsync(d => d.MatterId == matter.Id && d.FileId == fileId, cancellationToken);
            if (!attached)
            {
                db.MatterDocuments.Add(new MatterDocument
                {
                    TenantId = matter.TenantId,
                    MatterId = matter.Id,
                    FileId = fileId,
                    FileName = file.FileName,
                    Note = "synced from connector",
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (rag is not null)
        {
            var collectionId = await rag.GetOrCreateCollectionAsync(
                LegalModule.Id, MatterRagGate.MatterResourceType, matter.Id, $"matter: {matter.Name}", cancellationToken);
            foreach (var fileId in fileIds)
            {
                await rag.IngestFileAsync(collectionId, fileId, cancellationToken);
            }
        }
    }
}
