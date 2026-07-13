using Cortex.Application.Channels;
using Cortex.Application.Notifications;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Channels;

/// <summary>
/// The email-intake channel (docs/INBOUND_CHANNELS.md): each poll turns new mail in the org's
/// intake mailbox into authorized agent turns through the channel-agnostic
/// <see cref="IChannelTurnService"/> — correspondents JIT-provision as <c>email:{address}</c>
/// users (seat gate included), attachments land in the tenant file store, and, when replies are
/// enabled, the agent's answer goes back out through the platform's SMTP transport.
/// </summary>
public sealed class EmailChannelService(
    IChannelTurnService turns,
    IImapInbox inbox,
    ISmtpTransport smtp,
    PlatformDbContext db,
    IOptions<EmailChannelOptions> options,
    ILogger<EmailChannelService> logger)
{
    public const string ChannelId = "email";

    /// <summary>
    /// One poll: fetch mail newer than the persisted watermark, run a turn per message, then advance
    /// the watermark. The watermark only moves after the batch completes, so a crash mid-batch
    /// redelivers (at-least-once) rather than losing mail.
    /// </summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        var o = options.Value;

        var cursor = await db.ChannelCursors.FindAsync([ChannelId], cancellationToken);
        var result = await inbox.FetchNewAsync(cursor?.Watermark, cancellationToken);

        var processed = 0;
        foreach (var mail in result.Messages)
        {
            // Never answer ourselves — a reply loop with an auto-responder would be unstoppable.
            if (string.Equals(mail.FromAddress, o.Username, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await ProcessMessageAsync(mail, o, cancellationToken);
            processed++;
        }

        if (result.NextWatermark is not null && result.NextWatermark != cursor?.Watermark)
        {
            if (cursor is null)
            {
                db.ChannelCursors.Add(new ChannelCursor { ChannelId = ChannelId, Watermark = result.NextWatermark });
            }
            else
            {
                cursor.Watermark = result.NextWatermark;
                cursor.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return processed;
    }

    private async Task ProcessMessageAsync(InboundEmail mail, EmailChannelOptions o, CancellationToken cancellationToken)
    {
        // Subject line included — for intake mail it often carries the actual ask.
        var text = string.IsNullOrWhiteSpace(mail.Subject)
            ? mail.TextBody ?? ""
            : $"Subject: {mail.Subject}\n\n{mail.TextBody}".TrimEnd();

        var result = await turns.RunAsync(new ChannelTurnRequest
        {
            ChannelId = ChannelId,
            TenantSlug = o.TenantSlug!,
            ModuleId = o.ModuleId!,
            ExternalId = mail.FromAddress.ToLowerInvariant(),
            AllowUserProvisioning = o.AllowUnknownSenders || o.AllowedSenders.Contains(
                mail.FromAddress, StringComparer.OrdinalIgnoreCase),
            DisplayName = mail.FromName,
            Text = text,
            Attachments = [.. mail.Attachments.Select(a =>
                new InboundAttachment(a.FileName, a.ContentType, new MemoryStream(a.Content)))],
        }, cancellationToken);

        if (result.Status is ChannelTurnStatus.TenantUnavailable or ChannelTurnStatus.IdentityRefused)
        {
            return; // dropped and logged by the turn service; intake mail gets no bounce
        }

        if (!o.ReplyEnabled)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Email intake processed a message from {From} (status {Status}); replies are disabled.",
                    mail.FromAddress, result.Status);
            }

            return;
        }

        var replyBody = result.Status switch
        {
            ChannelTurnStatus.Completed when !string.IsNullOrWhiteSpace(result.Reply) => result.Reply,
            ChannelTurnStatus.NoChatAccess =>
                "You don't have access to the assistant yet. Please contact your administrator.",
            ChannelTurnStatus.Failed =>
                "Sorry, I couldn't process that request. Please try again later.",
            _ => null,
        };

        if (replyBody is null)
        {
            return;
        }

        if (!smtp.IsConfigured)
        {
            logger.LogWarning(
                "Email intake wants to reply to {From} but no SMTP transport is configured (Email: section).",
                mail.FromAddress);
            return;
        }

        var subject = mail.Subject is { Length: > 0 } s
            ? (s.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? s : $"Re: {s}")
            : "Re: your message";
        await smtp.SendAsync(new EmailMessage(mail.FromAddress, subject, replyBody), cancellationToken);
    }
}
