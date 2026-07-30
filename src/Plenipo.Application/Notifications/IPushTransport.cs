using Plenipo.Core.Platform;

namespace Plenipo.Application.Notifications;

/// <summary>One notification bound for one registered device.</summary>
/// <param name="Token">The device's push token.</param>
/// <param name="Platform">Which push service the token belongs to.</param>
/// <param name="Title">Notification title, already subject to <see cref="PushOptions.IncludeContent"/>.</param>
/// <param name="Body">Notification body, likewise.</param>
/// <param name="Category">The producing category, so the client can route and the user can mute.</param>
/// <param name="Link">
/// Where tapping the notification should land — the same app-relative link the in-app inbox row
/// carries, which a shell resolves against the module manifest's routes.
/// </param>
public sealed record PushMessage(
    string Token,
    DevicePlatform Platform,
    string Title,
    string Body,
    string Category,
    string? Link);

/// <summary>What became of one <see cref="PushMessage"/>.</summary>
public enum PushDeliveryStatus
{
    /// <summary>Accepted by the push service.</summary>
    Delivered,

    /// <summary>
    /// The token is permanently invalid — the app was uninstalled, or the token was reissued. The
    /// channel deletes the device row on this, which is the only way a device list stays honest.
    /// </summary>
    TokenGone,

    /// <summary>A transient or unclassified failure. The device is kept and the next send retries.</summary>
    Failed,
}

/// <param name="Token">The token this outcome is for.</param>
/// <param name="Status">What happened.</param>
/// <param name="Error">The push service's message, for logs. Never surfaced to a caller.</param>
public sealed record PushResult(string Token, PushDeliveryStatus Status, string? Error = null);

/// <summary>
/// Delivers push messages to a push service. One registration swaps Expo for FCM/APNs directly, a
/// corporate MDM gateway, or a recording fake in tests — the same shape as the platform's other
/// infrastructure seams (<c>ISecretVault</c>, <c>IOcrEngine</c>, <c>ISmtpTransport</c>).
/// <para>
/// Batched because every real push service is: a fan-out to one user's four devices should be one
/// request, not four.
/// </para>
/// </summary>
public interface IPushTransport
{
    /// <summary>
    /// Sends every message and reports an outcome per token. Implementations should not throw for
    /// a single bad token — return <see cref="PushDeliveryStatus.Failed"/> for it instead, so one
    /// dead device cannot suppress the others.
    /// </summary>
    public Task<IReadOnlyList<PushResult>> SendAsync(
        IReadOnlyList<PushMessage> messages,
        CancellationToken cancellationToken = default);
}
