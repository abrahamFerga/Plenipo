using Cortex.Application.Channels;
using Cortex.Infrastructure.Channels;
using Microsoft.Extensions.Options;

namespace Cortex.AspNetCore.Channels;

/// <summary>
/// The WhatsApp adapter of the inbound-channel lane (docs/INBOUND_CHANNELS.md): turns a verified
/// webhook delivery into <see cref="ChannelTurnRequest"/>s for the channel-agnostic
/// <see cref="IChannelTurnService"/> — which owns tenant resolution, JIT provisioning (subject
/// <c>whatsapp:{phone}</c>, seat gate included), permissions, attachment storage, and the agent
/// turn — keeping only what is Meta-specific here: payload shapes, media download, and replies
/// in the channel's own voice via <see cref="IWhatsAppSender"/>.
/// </summary>
public sealed class WhatsAppChannelService(
    IChannelTurnService turns,
    IWhatsAppSender sender,
    IWhatsAppMediaClient media,
    IOptions<WhatsAppOptions> options)
{
    public const string ChannelId = "whatsapp";

    public async Task ProcessAsync(WhatsAppWebhookPayload payload, CancellationToken cancellationToken)
    {
        foreach (var entry in payload.Entries ?? [])
        {
            foreach (var change in entry.Changes ?? [])
            {
                if (!string.Equals(change.Field, "messages", StringComparison.OrdinalIgnoreCase) ||
                    change.Value?.Messages is not { } messages)
                {
                    continue; // delivery statuses and other webhook fields — nothing to answer
                }

                foreach (var message in messages)
                {
                    await ProcessMessageAsync(change.Value, message, cancellationToken);
                }
            }
        }
    }

    private async Task ProcessMessageAsync(
        WhatsAppWebhookPayload.ChangeValue value,
        WhatsAppWebhookPayload.Message message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.From))
        {
            return;
        }

        var kind = message.Type?.ToLowerInvariant();
        var isText = kind == "text" && !string.IsNullOrWhiteSpace(message.Text?.Body);
        var mediaRef = kind switch
        {
            "document" => message.Document,
            "image" => message.Image,
            _ => null,
        };

        if (!isText && mediaRef?.Id is null)
        {
            // Audio/location/etc. still deserve an answer — the sender is a real user waiting on a reply.
            await sender.SendTextAsync(
                message.From,
                "Sorry — I can only read text messages, documents, and images for now.",
                cancellationToken);
            return;
        }

        string text;
        List<InboundAttachment> attachments = [];
        if (mediaRef?.Id is not null)
        {
            var downloaded = await DownloadMediaAsync(mediaRef, cancellationToken);
            if (downloaded is null)
            {
                await sender.SendTextAsync(
                    message.From,
                    "Sorry — I couldn't download that attachment. Please try sending it again.",
                    cancellationToken);
                return;
            }

            attachments.Add(downloaded);
            text = mediaRef.Caption ?? ""; // blank → the turn service's default caption applies
        }
        else
        {
            text = message.Text!.Body!;
        }

        var result = await turns.RunAsync(new ChannelTurnRequest
        {
            ChannelId = ChannelId,
            TenantSlug = options.Value.TenantSlug!,
            ModuleId = options.Value.ModuleId!,
            ExternalId = message.From,
            DisplayName = value.Contacts?.FirstOrDefault(c => c.WaId == message.From)?.Profile?.Name
                ?? $"WhatsApp +{message.From}",
            Text = text,
            Attachments = attachments,
        }, cancellationToken);

        var reply = result.Status switch
        {
            ChannelTurnStatus.Completed => result.Reply,
            ChannelTurnStatus.NoChatAccess =>
                "You don't have access to the assistant yet. Please contact your administrator.",
            ChannelTurnStatus.Failed =>
                "Sorry, I couldn't process that request. Please try again later.",
            _ => null, // TenantUnavailable / IdentityRefused: drop silently, as before
        };

        if (!string.IsNullOrWhiteSpace(reply))
        {
            await sender.SendTextAsync(message.From, reply, cancellationToken);
        }
    }

    /// <summary>Downloads an inbound media object; the turn service takes stream ownership.</summary>
    private async Task<InboundAttachment?> DownloadMediaAsync(
        WhatsAppWebhookPayload.Media mediaRef, CancellationToken cancellationToken)
    {
        var downloaded = await media.DownloadAsync(mediaRef.Id!, cancellationToken);
        if (downloaded is null)
        {
            return null;
        }

        var contentType = downloaded.ContentType is "application/octet-stream" && mediaRef.MimeType is not null
            ? mediaRef.MimeType
            : downloaded.ContentType;
        var fileName = !string.IsNullOrWhiteSpace(mediaRef.FileName)
            ? mediaRef.FileName
            : $"whatsapp-{mediaRef.Id}{ExtensionFor(contentType)}";

        return new InboundAttachment(fileName, contentType, downloaded.Content);
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => "",
    };

    /// <summary>The WhatsApp spelling of the shared per-sender conversation id scheme.</summary>
    public static Guid ConversationIdForPhone(Guid tenantId, string phone) =>
        ChannelTurnService.ConversationIdFor(tenantId, $"{ChannelId}:{phone}");
}
