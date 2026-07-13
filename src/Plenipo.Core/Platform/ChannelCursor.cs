namespace Plenipo.Core.Platform;

/// <summary>
/// Where a polling inbound channel left off (e.g. the email channel's "UIDVALIDITY:lastUID" pair).
/// One row per channel id; deployment-scoped like the channel binding itself — a channel is bound
/// to one tenant via configuration, so the cursor needs no tenant column.
/// </summary>
public sealed class ChannelCursor
{
    /// <summary>The channel id, e.g. "email".</summary>
    public string ChannelId { get; set; } = "";

    /// <summary>Opaque, channel-defined resume position.</summary>
    public string? Watermark { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
