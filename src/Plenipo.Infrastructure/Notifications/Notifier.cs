using Plenipo.Application.Notifications;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// The default notifier: the in-app row is written first (the notification is durable the moment
/// NotifyAsync returns), then every registered <see cref="INotificationChannel"/> gets a
/// best-effort send — a webhook being down must neither lose the notification nor fail the
/// producer (a job completion, a reminder tick).
/// </summary>
public sealed class Notifier(
    PlatformDbContext db,
    IEnumerable<INotificationChannel> channels,
    ILogger<Notifier> logger) : INotifier
{
    public async Task NotifyAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        // The user's per-category mute: a disabled preference suppresses the notification entirely
        // (in-app AND channels). Producers often run outside a request scope, so the tenant filter
        // is applied explicitly rather than relying on an ambient tenant.
        var muted = await db.UserNotificationPreferences.IgnoreQueryFilters().AnyAsync(
            p => p.TenantId == notification.TenantId
                && p.UserId == notification.UserId
                && p.Category == notification.Category
                && !p.Enabled,
            cancellationToken);
        if (muted)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Notification '{Category}' suppressed by user preference", notification.Category);
            }
            return;
        }

        db.UserNotifications.Add(new UserNotification
        {
            TenantId = notification.TenantId,
            UserId = notification.UserId,
            Category = notification.Category,
            Title = notification.Title,
            Body = notification.Body,
            Link = notification.Link,
        });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var channel in channels)
        {
            try
            {
                await channel.SendAsync(notification, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification channel {Channel} failed (in-app copy persisted)",
                    channel.GetType().Name);
            }
        }
    }
}
