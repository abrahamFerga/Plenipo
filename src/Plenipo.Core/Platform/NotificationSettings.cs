using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// Per-tenant notification delivery config (one row per tenant). The webhook secret follows the
/// platform's write-only contract: only a vault REFERENCE is stored here; the admin API accepts a
/// new value and reports "a secret is set", never the value.
/// </summary>
public sealed class NotificationSettings : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Where notification events POST to; null disables webhook delivery.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Vault reference to the HMAC signing secret (see ISecretVault); null = unsigned.</summary>
    public string? WebhookSecretRef { get; set; }
}
