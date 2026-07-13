using System.Globalization;
using Cortex.Application.Channels;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Cortex.Infrastructure.Channels;

/// <summary>
/// The production <see cref="IImapInbox"/>: connects to the configured mailbox per poll (no session
/// held between polls), fetches messages with UID greater than the watermark, and returns them with
/// the new "UIDVALIDITY:lastUID" watermark. A changed UIDVALIDITY means the server rebuilt the
/// folder's UID space — the watermark resets to the folder's current end, so nothing old replays.
/// </summary>
public sealed class MailKitImapInbox(IOptions<EmailChannelOptions> options) : IImapInbox
{
    public async Task<ImapPollResult> FetchNewAsync(string? watermark, CancellationToken cancellationToken = default)
    {
        var o = options.Value;

        using var client = new ImapClient();
        // Options are validated fail-fast at startup — enabled implies these are set.
        await client.ConnectAsync(o.Host!, o.Port, o.UseSsl, cancellationToken);
        await client.AuthenticateAsync(o.Username!, o.Password!, cancellationToken);

        var folder = await client.GetFolderAsync(o.Folder, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var validity = folder.UidValidity;
        var lastUid = ParseWatermark(watermark, validity);

        IList<UniqueId> newUids;
        if (lastUid is null)
        {
            // First poll (or rebuilt folder): don't replay the whole mailbox — start after what exists now.
            newUids = [];
        }
        else
        {
            var range = new UniqueIdRange(new UniqueId(validity, lastUid.Value + 1), UniqueId.MaxValue);
            newUids = await folder.SearchAsync(range, SearchQuery.All, cancellationToken);
        }

        var messages = new List<InboundEmail>(newUids.Count);
        var maxUid = lastUid ?? (await HighestExistingUidAsync(folder, cancellationToken));
        var sizes = newUids.Count == 0
            ? []
            : await folder.FetchAsync(newUids, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, cancellationToken);
        var sizeByUid = sizes.ToDictionary(s => s.UniqueId, s => (long?)s.Size);

        foreach (var uid in newUids)
        {
            maxUid = Math.Max(maxUid, uid.Id);
            // Fail closed if the server omits RFC822.SIZE: downloading first and checking later
            // would let an untrusted sender consume unbounded memory before MIME parsing.
            if (!sizeByUid.TryGetValue(uid, out var size) || size is null or <= 0 || size > o.MaxMessageBytes)
            {
                continue;
            }

            var mime = await folder.GetMessageAsync(uid, cancellationToken);
            var from = mime.From.Mailboxes.FirstOrDefault();
            if (from is null)
            {
                continue;
            }

            var attachments = new List<InboundEmailAttachment>();
            foreach (var part in mime.Attachments.OfType<MimePart>())
            {
                if (part.Content is null)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                await part.Content.DecodeToAsync(buffer, cancellationToken);
                attachments.Add(new InboundEmailAttachment(
                    part.FileName ?? $"attachment-{uid.Id}",
                    part.ContentType?.MimeType ?? "application/octet-stream",
                    buffer.ToArray()));
            }

            messages.Add(new InboundEmail(
                MessageId: mime.MessageId ?? $"{validity}:{uid.Id}",
                FromAddress: from.Address,
                FromName: string.IsNullOrWhiteSpace(from.Name) ? null : from.Name,
                Subject: mime.Subject,
                TextBody: mime.TextBody ?? mime.HtmlBody,
                Attachments: attachments));
        }

        await client.DisconnectAsync(quit: true, cancellationToken);

        return new ImapPollResult(messages, $"{validity}:{maxUid}");
    }

    /// <summary>Returns the last-seen UID, or null when there is no usable watermark for this folder
    /// generation (first poll, or the folder's UIDVALIDITY changed).</summary>
    private static uint? ParseWatermark(string? watermark, uint currentValidity)
    {
        if (watermark?.Split(':') is [var validity, var uid] &&
            uint.TryParse(validity, NumberStyles.None, CultureInfo.InvariantCulture, out var v) &&
            v == currentValidity &&
            uint.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out var u))
        {
            return u;
        }

        return null;
    }

    private static async Task<uint> HighestExistingUidAsync(IMailFolder folder, CancellationToken cancellationToken)
    {
        if (folder.UidNext is { } next && next.Id > 0)
        {
            return next.Id - 1;
        }

        var all = await folder.SearchAsync(SearchQuery.All, cancellationToken);
        return all.Count > 0 ? all.Max(u => u.Id) : 0;
    }
}
