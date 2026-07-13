using Plenipo.Application.Notifications;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// Looks up a tenant's webhook config by EXPLICIT tenant id, bypassing the ambient tenant filter —
/// notification producers (the job processor) run in scopes with no request identity.
/// </summary>
public sealed class NotificationWebhookConfigReader(PlatformDbContext db) : INotificationWebhookConfigReader
{
    public async Task<NotificationWebhookConfig?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var row = await db.NotificationSettings.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        return row?.WebhookUrl is { Length: > 0 }
            ? new NotificationWebhookConfig(row.WebhookUrl, row.WebhookSecretRef)
            : null;
    }
}
