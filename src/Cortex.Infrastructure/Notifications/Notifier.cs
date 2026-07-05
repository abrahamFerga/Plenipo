using Cortex.Application.Notifications;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure.Notifications;

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
