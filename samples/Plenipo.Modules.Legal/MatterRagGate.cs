using Plenipo.Application.Rag;
using Plenipo.Core.Identity;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Legal;

/// <summary>
/// The legal module's collection gate: a matter's knowledge collection is queryable exactly by
/// whoever can see the matter — tenant scoping via the module's query filters plus the ethical
/// wall. This is the coarse "scope-first" layer of the RAG authorization model; a deleted matter,
/// a foreign tenant's matter, and a wall all read as "no".
/// </summary>
public sealed class MatterRagGate(LegalDbContext db, ICurrentUser currentUser) : IRagCollectionGate
{
    public const string MatterResourceType = "matter";

    public string ResourceType => MatterResourceType;

    public async Task<bool> CanQueryAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == resourceId, cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId);
    }
}
