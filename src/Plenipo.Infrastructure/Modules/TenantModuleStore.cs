using Plenipo.Application.Modules;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Modules;

/// <summary>
/// Reads per-tenant module enablement from the platform database. The <c>TenantModule</c> global query
/// filter scopes every query to the ambient tenant, and enablement is default-on: only an explicit
/// <c>IsEnabled = false</c> row disables a module, so an unseeded tenant sees everything.
/// </summary>
public sealed class TenantModuleStore(PlatformDbContext db) : ITenantModuleStore
{
    public async Task<IReadOnlySet<string>> GetDisabledModuleIdsAsync(CancellationToken cancellationToken = default)
    {
        var disabled = await db.TenantModules
            .Where(tm => !tm.IsEnabled)
            .Select(tm => tm.ModuleId)
            .ToListAsync(cancellationToken);
        return disabled.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> IsEnabledAsync(string moduleId, CancellationToken cancellationToken = default) =>
        !await db.TenantModules.AnyAsync(tm => tm.ModuleId == moduleId && !tm.IsEnabled, cancellationToken);
}
