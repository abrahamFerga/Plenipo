namespace Cortex.Application.Channels;

/// <summary>
/// WhatsApp channel configuration (Meta WhatsApp Business Cloud API), bound from the
/// "Channels:WhatsApp" section. Disabled by default; when enabled the platform exposes a webhook that
/// turns inbound WhatsApp text messages into authorized agent turns and sends the reply back over
/// WhatsApp. Secrets (the app secret and access token) come from Key Vault / user-secrets, never source.
/// </summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "Channels:WhatsApp";

    public bool Enabled { get; set; }

    /// <summary>The token echoed back during Meta's webhook verification handshake (you choose it).</summary>
    public string? VerifyToken { get; set; }

    /// <summary>The Meta app secret — used to verify the X-Hub-Signature-256 HMAC on every delivery.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Bearer token for the Cloud API (a system-user token in production).</summary>
    public string? AccessToken { get; set; }

    /// <summary>The WhatsApp Business phone-number id that sends replies.</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>
    /// Cloud API base URL. Overridable so tests (and regional deployments) can point the sender at a
    /// stand-in server — the E2E suite fakes the whole channel without any Meta credentials.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/v21.0";

    /// <summary>The module whose agent answers WhatsApp conversations (e.g. "finance").</summary>
    public string? ModuleId { get; set; }

    /// <summary>The tenant slug WhatsApp users are provisioned into.</summary>
    public string? TenantSlug { get; set; }

    /// <summary>WhatsApp's per-message text limit; longer replies are split into consecutive messages.</summary>
    public int MaxMessageLength { get; set; } = 4096;

    /// <summary>Allow any WhatsApp sender to create a Cortex user. Unsafe for private workspaces; off by default.</summary>
    public bool AllowUnknownSenders { get; set; }

    /// <summary>Phone numbers allowed to JIT-provision when <see cref="AllowUnknownSenders"/> is false.</summary>
    public string[] AllowedSenders { get; set; } = [];

    /// <summary>Throws when the channel is enabled but missing a setting it cannot run without.</summary>
    public void ThrowIfInvalid()
    {
        if (!Enabled)
        {
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(VerifyToken)) missing.Add(nameof(VerifyToken));
        if (string.IsNullOrWhiteSpace(AppSecret)) missing.Add(nameof(AppSecret));
        if (string.IsNullOrWhiteSpace(AccessToken)) missing.Add(nameof(AccessToken));
        if (string.IsNullOrWhiteSpace(PhoneNumberId)) missing.Add(nameof(PhoneNumberId));
        if (string.IsNullOrWhiteSpace(ModuleId)) missing.Add(nameof(ModuleId));
        if (string.IsNullOrWhiteSpace(TenantSlug)) missing.Add(nameof(TenantSlug));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The WhatsApp channel is enabled but misconfigured — missing: {string.Join(", ", missing)}. " +
                $"Set Channels:WhatsApp:* (secrets via user-secrets or Key Vault) or set Channels:WhatsApp:Enabled=false.");
        }
    }
}
