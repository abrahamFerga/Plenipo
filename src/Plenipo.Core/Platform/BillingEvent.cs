namespace Plenipo.Core.Platform;

/// <summary>
/// The billing webhook inbox: every event the payment provider delivers, persisted raw BEFORE any
/// processing ("receive fast, process safe"). The unique (Provider, EventId) makes redelivery and
/// replay harmless; a background worker processes rows idempotently and records the outcome here.
/// Not tenant-owned — a purchase event arrives before its tenant exists.
/// </summary>
public sealed class BillingEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The payment provider ("stripe").</summary>
    public required string Provider { get; init; }

    /// <summary>The provider's event id (e.g. "evt_…") — the idempotency key.</summary>
    public required string EventId { get; init; }

    /// <summary>The provider's event type (e.g. "checkout.session.completed").</summary>
    public required string Type { get; init; }

    /// <summary>The raw event payload as delivered (signature was verified against these bytes).</summary>
    public required string PayloadJson { get; init; }

    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the worker finished with this event (null = pending).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>The last processing error, if any (kept for triage; retried up to a bound).</summary>
    public string? Error { get; set; }

    public int Attempts { get; set; }
}
