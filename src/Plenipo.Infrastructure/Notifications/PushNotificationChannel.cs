using Plenipo.Application.Notifications;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// Fans a notification out to the recipient's registered devices. Like every channel this is
/// best-effort: the in-app inbox row is already committed by the time the notifier gets here, so a
/// push service outage delays a buzz, it never loses a notification.
/// <para>
/// The channel is registered unconditionally and does nothing until a device registers, so a
/// deployment with no mobile app pays nothing for it.
/// </para>
/// </summary>
public sealed class PushNotificationChannel(
    PlatformDbContext db,
    IPushTransport transport,
    IOptions<PushOptions> options,
    ILogger<PushNotificationChannel> logger) : INotificationChannel
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        // The notifier runs outside a request scope (a job tick, a webhook), where there is no
        // ambient tenant for the global query filter to read — so the tenant is applied explicitly,
        // exactly as Notifier does for the preference lookup.
        var devices = await db.UserDevices
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == notification.TenantId && d.UserId == notification.UserId)
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        // What actually leaves the deployment. With IncludeContent off the push service — and
        // anyone reading the lock screen — learns only that something arrived; the app fetches the
        // real content over its authenticated session after the tap.
        var title = settings.IncludeContent ? notification.Title : settings.PlaceholderTitle;
        var body = settings.IncludeContent ? notification.Body : settings.PlaceholderBody;

        var messages = devices
            .Select(d => new PushMessage(d.PushToken, d.Platform, title, body, notification.Category, notification.Link))
            .ToList();

        var results = await transport.SendAsync(messages, cancellationToken);

        // A token the service calls permanently gone is deleted. This is the only thing that keeps
        // a device list from filling with uninstalled apps, and it is safe to do inline: the row
        // carries nothing but routing information.
        var gone = results
            .Where(r => r.Status == PushDeliveryStatus.TokenGone)
            .Select(r => r.Token)
            .ToHashSet(StringComparer.Ordinal);
        if (gone.Count > 0)
        {
            db.UserDevices.RemoveRange(devices.Where(d => gone.Contains(d.PushToken)));
            await db.SaveChangesAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Removed {Count} push device(s) the push service reported as gone", gone.Count);
            }
        }

        foreach (var failure in results.Where(r => r.Status == PushDeliveryStatus.Failed))
        {
            // No token in the log line — it identifies a device.
            logger.LogWarning("Push delivery failed for one device: {Error}", failure.Error ?? "unspecified");
        }
    }
}
