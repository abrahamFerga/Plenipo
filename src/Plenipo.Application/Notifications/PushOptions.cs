namespace Plenipo.Application.Notifications;

/// <summary>
/// Push delivery settings, bound from the "Push" section. Like email, the channel is built in and
/// inert until there is something to do — with no device registered, nothing is sent and nothing
/// needs configuring, so a deployment that never ships a mobile app never thinks about this.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>
    /// Master switch. On by default because the channel is already inert without registered
    /// devices; turning it off is the operator's kill switch for push specifically, leaving the
    /// in-app inbox and every other channel untouched.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the notification's title and body travel to the push service, or only a neutral
    /// placeholder does.
    /// <para>
    /// This is a real privacy decision, not a formatting one: a push provider is a third party, and
    /// a lock-screen preview is readable by anyone holding the phone. A deployment handling
    /// privileged material (a legal matter, a diagnosis) sets this to <c>false</c> — the device
    /// gets "You have a new notification", taps through, and the app fetches the actual content
    /// from the inbox over its authenticated session. The default is <c>true</c> because a useless
    /// notification is its own failure, and most content is not sensitive.
    /// </para>
    /// </summary>
    public bool IncludeContent { get; set; } = true;

    /// <summary>Title shown when <see cref="IncludeContent"/> is off.</summary>
    public string PlaceholderTitle { get; set; } = "New notification";

    /// <summary>Body shown when <see cref="IncludeContent"/> is off.</summary>
    public string PlaceholderBody { get; set; } = "Open the app to read it.";

    /// <summary>
    /// Expo's push endpoint, used by the built-in transport. Overridable so a deployment can point
    /// at a self-hosted relay; it goes through the platform's outbound URL policy either way.
    /// </summary>
    public string ExpoEndpoint { get; set; } = "https://exp.host/--/api/v2/push/send";

    /// <summary>
    /// Optional Expo access token, required only when the Expo project enforces one. Write-only in
    /// the platform's sense: it comes from configuration (user-secrets / Key Vault) and is never
    /// echoed back by any endpoint.
    /// </summary>
    public string? ExpoAccessToken { get; set; }

    /// <summary>
    /// Most devices one user may register. A generous cap that exists so a looping client cannot
    /// grow the table without bound; registering past it evicts the least recently seen device.
    /// </summary>
    public int MaxDevicesPerUser { get; set; } = 10;
}
