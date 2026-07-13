# Inbound channels: from a WhatsApp lane to a channel SDK

Design for generalizing the WhatsApp inbound lane into a host-extensible **inbound-channel
SDK** — the missing half of channel extensibility (outbound notification channels are already
open via `AddPlenipoNotificationChannel`). The concrete payoff: products add SMS, Telegram, or
**email intake (IMAP)** without forking, and WhatsApp becomes the SDK's first adapter instead
of a special case.

## What the WhatsApp lane does today (and what generalizes)

`WhatsAppChannelService` (274 lines) interleaves two kinds of logic:

| Channel-specific (stays in the adapter) | Generic (extracts into the SDK) |
|---|---|
| Meta webhook GET verification + `X-Hub-Signature-256` HMAC | Tenant resolution by configured slug + deactivated-tenant refusal |
| `WhatsAppWebhookPayload` parsing | Allowlisted external identity (`whatsapp:{phone}`); optional operator-enabled JIT provisioning incl. the **seat gate** |
| Media download via the Graph media API | Attachment → tenant file store → `[Attached files]` block convention |
| Reply via `IWhatsAppSender` | The agent turn: stable per-identity conversation, module binding, `AgentRunRequest`, reply text collection |

## The contract

```csharp
// One inbound message, however it arrived (webhook push or poll pull).
public sealed record InboundMessage(
    string ExternalId,                 // provider's message id (dedup key for at-least-once transports)
    string Identity,                   // stable subject, e.g. "whatsapp:+52551234" / "email:client@x.com"
    string? DisplayName,
    string Text,
    IReadOnlyList<InboundAttachment> Attachments);   // name + content stream + content type

public sealed record InboundAttachment(string FileName, string ContentType, Stream Content);

// The generic core, extracted from WhatsAppChannelService — ONE implementation for all channels:
// resolve tenant → refuse deactivated/unknown sender → optionally JIT-provision identity (seat gate applies) → store
// attachments → run the module-bound agent turn → return the reply text.
public interface IChannelTurnService
{
    Task<string?> RunAsync(string tenantSlug, string moduleId, InboundMessage message, CancellationToken ct);
}

// A channel adapter declares itself and registers its services (mirrors IConnector/IModule).
public interface IInboundChannel
{
    InboundChannelManifest Manifest { get; }          // id, display name, transport kind
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
}

// Webhook-push transports (WhatsApp, Telegram, Twilio SMS): the SDK maps
// /channels/{id}/webhook and delegates verification + parsing to the adapter.
public interface IWebhookChannelAdapter
{
    Task<IResult?> VerifyAsync(HttpRequest request);                       // e.g. Meta's GET challenge
    bool IsAuthentic(ReadOnlySpan<byte> body, IHeaderDictionary headers);  // signature over the RAW body
    IReadOnlyList<InboundMessage> Parse(ReadOnlySpan<byte> body);
}

// Poll-pull transports (IMAP): the SDK schedules PollAsync on the jobs primitive and persists
// the watermark; the adapter only speaks its protocol.
public interface IPollingChannelAdapter
{
    Task<ChannelPollResult> PollAsync(string? watermark, CancellationToken ct);
}
public sealed record ChannelPollResult(IReadOnlyList<InboundMessage> Messages, string? NextWatermark);

// Replies go back out through the adapter (absent = intake-only channel, no reply).
public interface IChannelReplySender
{
    Task SendAsync(string identity, string text, CancellationToken ct);
}
```

Host registration mirrors every other seam: `builder.AddPlenipoInboundChannel<TelegramChannel>()`.
Configuration stays per channel (`Channels:{id}:…`), including the tenant binding and target
module — multi-tenant routing (one number/mailbox per tenant) is a follow-up, same as WhatsApp
today.

## Email intake (IMAP) on this SDK

The lawyer scenario: clients email the firm's intake address; the message and its attachments
land as an agent turn ("client intake" with documents filed), optionally answered by email.

- **Transport**: polling. The SDK schedules the poll through the existing jobs primitive
  (lease recovery and attempts for free); the watermark is the mailbox's `UIDVALIDITY:lastUID`
  pair, so a rebuilt mailbox restarts cleanly and nothing is processed twice.
- **Settings** (tenant-level service auth — the intake mailbox belongs to the firm):
  host, port, username, **password (secret, write-only)**, folder (default INBOX), and the
  bound module. Per-user OAuth mailboxes are explicitly out of scope: intake is an org mailbox.
- **Identity**: `email:{from-address}` — allowlisted correspondents are accepted; an operator may explicitly enable JIT provisioning
  number does, seat gate included; `*@…` display name from the From header.
- **Attachments**: MIME parts → tenant file store → the standard `[Attached files]` block, so
  document tools/matters/RAG work unchanged.
- **Reply**: through the existing `ISmtpTransport` seam (subject `Re:` threading) — off by
  default; intake-only is the safe start.
- **Protocol client**: MailKit (MIT) behind an `IImapClient` seam — keyless tests fake the
  seam with canned MIME messages; the watermark/dedup/turn plumbing stays fully real.

## Migration plan (each step shippable, behavior-identical)

1. **Extract `ChannelTurnService`** from `WhatsAppChannelService` — no contract change, the
   WhatsApp tests keep passing untouched. (The seat gate and JIT code stop being duplicated.)
   **✅ Shipped** — `IChannelTurnService` in `Plenipo.Application/Channels`, the implementation in
   `Plenipo.Infrastructure/Channels/ChannelTurnService.cs`; WhatsApp is now a thin adapter.
2. **Introduce the SDK contracts** + rewrite WhatsApp as the first `IWebhookChannelAdapter`
   (its payload/signature/media code moves into the adapter; the endpoint mapping generalizes
   to `/channels/{id}/webhook` with `/whatsapp/webhook` kept as an alias).
3. **Ship email intake** as the first polling adapter (fake `IImapClient` tests: new mail →
   provisioned identity → turn with attachments → optional SMTP reply; watermark survival).
   **✅ Shipped** — built directly on `IChannelTurnService` (a polling channel needs no webhook, so
   step 2 wasn't a prerequisite): `Channels:Email` options, `IImapInbox` seam with a MailKit
   implementation, `EmailChannelService.PollOnceAsync` + a `PeriodicTimer` poller hosted service
   (the jobs primitive can take over when multi-instance leasing matters), and the
   `channel_cursors` table persisting the `UIDVALIDITY:lastUID` watermark.
4. **Document** the seam in BUILDING_A_PRODUCT.md and retire the "inbound channels" entry
   from the not-extensible list.

## Why not fold email into the connector SDK?

Connectors answer "reach data the tenant already has" (pull, on demand or synced); channels
answer "people talk to the product from outside" (conversations, identities, seats, replies).
Email intake is conversational — it can provision operator-approved people and runs agent turns — so it belongs
to the channel lane. An IMAP *connector* ("index this mailbox folder into knowledge") remains
possible later on the connector SDK; different job, different seam.
