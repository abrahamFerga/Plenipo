using Plenipo.Application.Ai;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Ai;

/// <summary>
/// Reads the current tenant's <c>TenantAiSettings</c> row (scoped by the global query filter) and layers it
/// over the deployment <see cref="AiOptions"/> defaults. A tenant with no row — or with null fields — simply
/// gets the defaults, so this is transparent until an admin customizes anything.
/// </summary>
public sealed class TenantAiSettingsResolver(PlatformDbContext db, IOptions<AiOptions> aiOptions) : ITenantAiSettings
{
    public async Task<EffectiveAiSettings> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.TenantAiSettings.FirstOrDefaultAsync(cancellationToken);
        return EffectiveAiSettings.Merge(row, aiOptions.Value);
    }
}
