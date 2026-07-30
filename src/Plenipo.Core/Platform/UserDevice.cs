using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>Which push service a device's token belongs to.</summary>
public enum DevicePlatform
{
    /// <summary>Apple, reached through whichever push service the deployment configures.</summary>
    Ios,

    /// <summary>Android, likewise.</summary>
    Android,

    /// <summary>A browser or PWA using the Web Push protocol.</summary>
    Web,
}

/// <summary>
/// One installation of a Plenipo client that has asked to receive push notifications — the mobile
/// shell registers itself here after the user grants permission, and the push channel fans a
/// notification out to every device the recipient still has.
/// <para>
/// Identity is the <see cref="InstallationId"/>, not the token: push tokens rotate (an OS update, a
/// reinstall, a backup restore) while the installation stays the same, so registering again with a
/// fresh token updates the row instead of accumulating dead ones. A token the push service reports
/// as gone is deleted outright — an unreachable token is not worth keeping, and a device
/// identifier is not something to hold onto for its own sake.
/// </para>
/// </summary>
public sealed class UserDevice : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// A stable identifier for this app installation, minted by the client and unchanged across
    /// token rotations. Unique per user.
    /// </summary>
    public required string InstallationId { get; set; }

    /// <summary>
    /// The push token to deliver to. A device identifier, so it is treated the way the platform
    /// treats other identifiers: never echoed back to any caller, and masked wherever it's shown.
    /// </summary>
    public required string PushToken { get; set; }

    public DevicePlatform Platform { get; set; }

    /// <summary>Human-readable device label ("Pixel 9", "iPhone"), for a "your devices" list.</summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Last time the client re-registered. Lets an operator (or a future sweep) recognise
    /// installations that have gone quiet without waiting for the push service to say so.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
