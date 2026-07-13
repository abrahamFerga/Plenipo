using System.Security.Claims;

namespace Plenipo.Application.Authorization;

/// <summary>
/// Resolves the full set of permission strings a principal holds, merging permissions carried as
/// claims in the token with per-tenant grants stored in the platform database. Implemented in the
/// infrastructure layer; cached per request.
/// </summary>
public interface IPermissionResolver
{
    public Task<IReadOnlySet<string>> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
