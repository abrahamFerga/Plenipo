namespace Cortex.Core.Platform;

/// <summary>The lifecycle of a paid subscription's access (docs/COMMERCIALIZATION.md).</summary>
public enum EntitlementStatus
{
    /// <summary>Payment landed; the tenant (or dedicated environment) is being created.</summary>
    Provisioning = 0,

    /// <summary>Paid and live.</summary>
    Active = 1,

    /// <summary>Payment failed; access suspended (tenant deactivated), nothing deleted.</summary>
    PastDue = 2,

    /// <summary>Subscription ended; in the grace window before deprovisioning.</summary>
    Canceled = 3,

    /// <summary>Destructive teardown done (after the grace window).</summary>
    Deprovisioned = 4,
}

/// <summary>
/// What a subscription buys: the link between the payment provider's subscription and the tenant
/// it provisioned, with the plan facts the platform enforces (seats, tier). Operator-level like
/// <see cref="Tenant"/> itself — not tenant-owned (it exists before and after the tenant does).
/// </summary>
public sealed class TenantEntitlement
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The product sold (e.g. "the-lawyer") — products are separate systems on one platform.</summary>
    public required string ProductId { get; set; }

    /// <summary>The product-defined plan key (e.g. "solo", "team", "dedicated").</summary>
    public required string Plan { get; set; }

    public EntitlementStatus Status { get; set; } = EntitlementStatus.Provisioning;

    /// <summary>The tenant this entitlement provisioned; null until provisioning completes.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Seats purchased (drives <see cref="Tenant.MaxSeats"/>). Null = plan default/unlimited.</summary>
    public int? Seats { get; set; }

    /// <summary>The provider's subscription id (e.g. "sub_…") — the lifecycle correlation key.</summary>
    public required string SubscriptionRef { get; set; }

    /// <summary>The provider's customer id (e.g. "cus_…").</summary>
    public string? CustomerRef { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the grace window ends and deprovisioning may run (set on cancellation).</summary>
    public DateTimeOffset? DeprovisionAfter { get; set; }
}
