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

    /// <summary>Dedicated-tier dispatch target (the deploy-customer workflow). Null = tier not offered.</summary>
    public DedicatedOptions? Dedicated { get; set; }

    /// <summary>SECRET — the Stripe API key used for meter events. User-secrets/Key Vault only.</summary>
    public string? StripeApiKey { get; set; }

    /// <summary>The Stripe billing meter's event_name that AI token usage reports against.</summary>
    public string MeterEventName { get; set; } = "cortex_ai_tokens";

    /// <summary>How often accumulated usage is pushed to the meter.</summary>
    public int UsageExportSeconds { get; set; } = 60;

    /// <summary>Stripe Price id per product per plan (Commerce:Prices:the-lawyer:team = "price_…"). Not secret.</summary>
    public Dictionary<string, Dictionary<string, string>> Prices { get; set; } = [];

    /// <summary>Where Stripe Checkout returns the buyer (e.g. the site's /welcome page).</summary>
    public string? CheckoutSuccessUrl { get; set; }

    /// <summary>Where an abandoned checkout returns (e.g. the pricing page).</summary>
    public string? CheckoutCancelUrl { get; set; }

    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(WebhookSecret);
}

/// <summary>Where dedicated-environment work is dispatched (GitHub workflow-dispatch).</summary>
public sealed class DedicatedOptions
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Workflow { get; set; } = "deploy-customer.yml";
    public string Ref { get; set; } = "main";

    /// <summary>SECRET — a token with actions:write on the repo. User-secrets/Key Vault only.</summary>
    public string Token { get; set; } = "";
}
