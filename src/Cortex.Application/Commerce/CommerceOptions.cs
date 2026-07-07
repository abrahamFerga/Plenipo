namespace Cortex.Application.Commerce;

/// <summary>
/// Billing configuration (the "Commerce" section). Off by default — a deployment that doesn't
/// sell subscriptions has no webhook surface at all. The webhook secret is a SECRET: user-secrets
/// in dev, Key Vault/environment in production, never appsettings.
/// </summary>
public sealed class CommerceOptions
{
    public const string SectionName = "Commerce";

    /// <summary>Master switch; also implied off when <see cref="WebhookSecret"/> is empty.</summary>
    public bool Enabled { get; set; }

    /// <summary>Stripe's signing secret for the webhook endpoint ("whsec_…").</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Seconds of clock skew tolerated on the signature timestamp (replay bound).</summary>
    public int SignatureToleranceSeconds { get; set; } = 300;

    /// <summary>Days a canceled subscription's data is kept before deprovisioning may run.</summary>
    public int CancellationGraceDays { get; set; } = 30;

    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(WebhookSecret);
}
