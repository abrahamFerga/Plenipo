using Plenipo.Application.Connectors;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>EF-backed bindings: one per resource; rebinding replaces the ref and resets sync state.</summary>
public sealed class ConnectorBindingService(PlatformDbContext db, ITenantContext tenant) : IConnectorBindingService
{
    public async Task<Guid> BindAsync(
        string connectorId, string moduleId, string resourceType, Guid resourceId, string externalRef,
        CancellationToken cancellationToken = default)
    {
        var binding = await db.ConnectorBindings.FirstOrDefaultAsync(
            b => b.ModuleId == moduleId && b.ResourceType == resourceType && b.ResourceId == resourceId,
            cancellationToken);
        if (binding is null)
        {
            binding = new ConnectorBinding
            {
                TenantId = tenant.RequireTenantId(),
                ConnectorId = connectorId,
                ModuleId = moduleId,
                ResourceType = resourceType,
                ResourceId = resourceId,
                ExternalRef = externalRef,
            };
            db.ConnectorBindings.Add(binding);
        }
        else
        {
            binding.ConnectorId = connectorId;
            binding.ExternalRef = externalRef;
            binding.SyncedItemsJson = null; // new location, fresh sync state
            binding.LastSyncedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return binding.Id;
    }

    public async Task<Guid?> FindAsync(
        string moduleId, string resourceType, Guid resourceId, CancellationToken cancellationToken = default)
    {
        var binding = await db.ConnectorBindings.FirstOrDefaultAsync(
            b => b.ModuleId == moduleId && b.ResourceType == resourceType && b.ResourceId == resourceId,
            cancellationToken);
        return binding?.Id;
    }
}
