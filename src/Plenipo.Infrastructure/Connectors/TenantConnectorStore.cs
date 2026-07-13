using Plenipo.Application.Connectors;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>
/// EF-backed per-tenant connector enablement. Default-OFF: only an explicit enabled row (written by
/// the admin Integrations surface) turns a connector on; the tenant global query filter scopes rows.
/// </summary>
public sealed class TenantConnectorStore(PlatformDbContext db) : ITenantConnectorStore
{
    public async Task<IReadOnlySet<string>> GetEnabledConnectorIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.TenantConnectors
            .Where(c => c.Enabled)
            .Select(c => c.ConnectorId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> IsEnabledAsync(string connectorId, CancellationToken cancellationToken = default) =>
        await db.TenantConnectors.AnyAsync(c => c.ConnectorId == connectorId && c.Enabled, cancellationToken);
}
