namespace Cortex.Application.Channels;

/// <summary>
/// One attachment arriving with an inbound channel message. The channel adapter fetches the bytes
/// (downloading is protocol-specific); the turn service stores them in the tenant file store and
/// takes ownership of the stream.
/// </summary>
public sealed record InboundAttachment(string FileName, string ContentType, Stream Content);

/// <summary>
/// An inbound message from an external conversation channel, normalized by the channel adapter.
/// This is the shared shape of the inbound-channel lane (docs/INBOUND_CHANNELS.md) — WhatsApp is
/// the first adapter; email intake, SMS, and Telegram compose the same request.
/// </summary>
public sealed record ChannelTurnRequest
{
    /// <summary>The channel id, e.g. "whatsapp". Prefixes the JIT subject (<c>{channelId}:{externalId}</c>),
    /// suffixes the synthetic email domain, and stamps files stored from attachments.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Slug of the tenant this channel is bound to.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>The module the agent turn runs against.</summary>
    public required string ModuleId { get; init; }

    /// <summary>The sender's stable identity within the channel (phone number, email address).</summary>
    public required string ExternalId { get; init; }

    /// <summary>Display name when the channel knows one; falls back to the subject.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The message text — or the attachment caption when the message is a file.</summary>
    public string Text { get; init; } = "";

    public IReadOnlyList<InboundAttachment> Attachments { get; init; } = [];
}

public enum ChannelTurnStatus
{
    /// <summary>The turn ran; <see cref="ChannelTurnResult.Reply"/> holds the agent's answer (possibly empty).</summary>
    Completed,

    /// <summary>The bound tenant is missing or deactivated — nothing to say, drop the message.</summary>
    TenantUnavailable,

    /// <summary>The sender is refused: a deactivated user, or the tenant's seat limit is reached.</summary>
    IdentityRefused,

    /// <summary>The sender exists but lacks chat access — the channel should say so in its own voice.</summary>
    NoChatAccess,

    /// <summary>The agent turn errored — the channel should apologize in its own voice.</summary>
    Failed,
}

public sealed record ChannelTurnResult(ChannelTurnStatus Status, string? Reply = null);

/// <summary>
/// The channel-agnostic core of every inbound conversation channel: resolve the bound tenant →
/// JIT-provision the sender as a platform user (seat gate included) → resolve permissions → store
/// attachments into the tenant file store → run the module-bound agent turn on the sender's stable
/// conversation → hand back the reply text. The agent runs with exactly the authority of that user:
/// tool filtering, auditing, token tracking, and the approval gate apply identically to the web UI.
/// </summary>
public interface IChannelTurnService
{
    public Task<ChannelTurnResult> RunAsync(ChannelTurnRequest request, CancellationToken cancellationToken = default);
}
