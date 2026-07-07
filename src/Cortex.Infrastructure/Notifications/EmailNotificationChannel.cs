using Cortex.Application.Notifications;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Notifications;

/// <summary>
/// Delivers each notification to the recipient's email address (looked up cross-tenant by the
/// explicit tenant/user on the notification — the notifier often runs outside a request scope).
/// A silent no-op until the "Email" section is configured; channel-synthesized addresses are
/// skipped — every inbound channel JIT-provisions its users with an unroutable
/// <c>{externalId}@{channelId}.channel</c> address (whatsapp, email, future adapters), and mailing
/// those would bounce or, worse, double-deliver to an intake mailbox. Fired from the notifier's
/// fan-out, which already isolates failures.
/// </summary>
public sealed class EmailNotificationChannel(
    ISmtpTransport smtp,
    IOptions<EmailOptions> options,
    PlatformDbContext db) : INotificationChannel
{
    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsEnabled)
        {
            return;
        }

        var email = await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == notification.TenantId && u.Id == notification.UserId && u.IsActive)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(email) || email.EndsWith(".channel", StringComparison.Ordinal))
        {
            return; // no real mailbox to deliver to
        }

        var body = notification.Body + (notification.Link is null ? "" : $"\n\n{notification.Link}");
        await smtp.SendAsync(new EmailMessage(email, notification.Title, body), cancellationToken);
    }
}
