using Plenipo.Application.Rag;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// The default principal set for chunk-level trimming: the platform user plus every role they hold
/// in the current tenant. Deliberately NOT permissions — a permission says what you may do, a
/// principal says who you are, and stamping documents with permissions would make "who can read
/// this memo" drift every time a role baseline is edited. A deployment that syncs source-system
/// groups (SharePoint, an ethical-wall system) replaces or decorates this registration.
/// </summary>
public sealed class RagPrincipalResolver(PlatformDbContext db, ICurrentUser currentUser) : IRagPrincipalResolver
{
    public async Task<IReadOnlyList<string>> GetPrincipalsAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not Guid userId)
        {
            return [];
        }

        var roles = await db.UserRoles
            .Where(r => r.UserId == userId)
            .Select(r => r.Role)
            .ToListAsync(cancellationToken);

        return [RagPrincipals.User(userId), .. roles.Select(RagPrincipals.Role)];
    }
}
