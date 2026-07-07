using System.Text.Json;
using Cortex.Application.Commerce;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Commerce;

/// <summary>
/// The process-safe half of the billing pipeline: drains the <see cref="BillingEvent"/> inbox in
/// the background. Idempotent at every layer — the inbox is unique per event, the entitlement is
/// unique per subscription, and a re-run of a half-processed event converges rather than
/// duplicating. Failures retry with a bound, then dead-letter with the error kept for triage.
/// Runs only when commerce is enabled.
/// </summary>
public sealed class BillingEventProcessor(
    IServiceScopeFactory scopes,
    IOptions<CommerceOptions> options,
    ILogger<BillingEventProcessor> logger) : BackgroundService
{
    private const int MaxAttempts = 5;

    /// <summary>Inbox scan cadence — short enough that a test polling for the outcome finishes fast.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Billing inbox drain failed; retrying next cycle.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var pending = await db.BillingEvents
            .Where(e => e.ProcessedAt == null && e.Attempts < MaxAttempts)
            .OrderBy(e => e.ReceivedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var evt in pending)
        {
            try
            {
                await ProcessAsync(db, provisioning, evt, ct);
                evt.ProcessedAt = DateTimeOffset.UtcNow;
                evt.Error = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                evt.Attempts++;
                evt.Error = ex.Message;
                if (evt.Attempts >= MaxAttempts)
                {
                    evt.ProcessedAt = DateTimeOffset.UtcNow; // dead-letter: recorded, no longer retried
                    logger.LogError(ex, "Billing event {EventId} dead-lettered after {Attempts} attempts.", evt.EventId, evt.Attempts);
                }
                else
                {
                    logger.LogWarning(ex, "Billing event {EventId} failed (attempt {Attempts}); will retry.", evt.EventId, evt.Attempts);
                }
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ProcessAsync(
        PlatformDbContext db, ITenantProvisioningService provisioning, BillingEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(db, provisioning, evt, ct);
                break;
            case "customer.subscription.updated":
                await HandleSubscriptionUpdatedAsync(db, evt, ct);
                break;
            case "invoice.payment_failed":
                await HandleBySubscriptionAsync(db, evt, "subscription", suspend: true, ct);
                break;
            case "invoice.paid":
                await HandleBySubscriptionAsync(db, evt, "subscription", suspend: false, ct);
                break;
            case "customer.subscription.deleted":
                await HandleSubscriptionDeletedAsync(db, evt, ct);
                break;
            default:
                // Types we don't act on are acknowledged so the inbox keeps draining.
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Billing event {EventId} of type {Type} acknowledged (no handler).", evt.EventId, evt.Type);
                }
                break;
        }
    }

    /// <summary>
    /// Plan/seat changes and provider-side status transitions. Seats come from the subscription's
    /// first item quantity (seat-based pricing); status "active" reactivates a suspended tenant,
    /// "past_due"/"unpaid" suspends it. Statuses we don't model are left to the invoice events.
    /// </summary>
    private async Task HandleSubscriptionUpdatedAsync(PlatformDbContext db, BillingEvent evt, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(evt.PayloadJson);
        var subscription = json.RootElement.GetProperty("data").GetProperty("object");
        var subscriptionRef = Str(subscription, "id")
            ?? throw new InvalidOperationException("subscription event has no id.");

        var entitlement = await FindEntitlementAsync(db, subscriptionRef, ct);
        if (entitlement is null)
        {
            return; // not a subscription we sold — acknowledged, not retried
        }

        // Seat resync: items.data[0].quantity is the seat count in seat-based pricing.
        if (subscription.TryGetProperty("items", out var items) &&
            items.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
            data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("quantity", out var q) && q.TryGetInt32(out var seats) && seats > 0)
        {
            entitlement.Seats = seats;
            if (entitlement.TenantId is { } tid)
            {
                var tenant = await db.Tenants.FirstAsync(t => t.Id == tid, ct);
                tenant.MaxSeats = seats;
            }
        }

        switch (Str(subscription, "status"))
        {
            case "active":
                await SetSuspendedAsync(db, entitlement, suspended: false, ct);
                break;
            case "past_due" or "unpaid":
                await SetSuspendedAsync(db, entitlement, suspended: true, ct);
                break;
        }

        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>invoice.payment_failed suspends; invoice.paid reactivates (dunning round-trip).</summary>
    private async Task HandleBySubscriptionAsync(
        PlatformDbContext db, BillingEvent evt, string subscriptionField, bool suspend, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(evt.PayloadJson);
        var invoice = json.RootElement.GetProperty("data").GetProperty("object");
        var subscriptionRef = Str(invoice, subscriptionField);
        if (subscriptionRef is null)
        {
            return; // one-off invoice, not a subscription's
        }

        var entitlement = await FindEntitlementAsync(db, subscriptionRef, ct);
        if (entitlement is null)
        {
            return;
        }

        await SetSuspendedAsync(db, entitlement, suspend, ct);
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Cancellation: suspend now, deprovision later — Canceled starts the grace window
    /// (CommerceOptions.CancellationGraceDays); the destructive teardown is a separate, delayed
    /// step that phase 6's deploy-customer workflow will honour.
    /// </summary>
    private async Task HandleSubscriptionDeletedAsync(PlatformDbContext db, BillingEvent evt, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(evt.PayloadJson);
        var subscription = json.RootElement.GetProperty("data").GetProperty("object");
        var subscriptionRef = Str(subscription, "id")
            ?? throw new InvalidOperationException("subscription event has no id.");

        var entitlement = await FindEntitlementAsync(db, subscriptionRef, ct);
        if (entitlement is null)
        {
            return;
        }

        entitlement.Status = EntitlementStatus.Canceled;
        entitlement.DeprovisionAfter = DateTimeOffset.UtcNow.AddDays(options.Value.CancellationGraceDays);
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
        if (entitlement.TenantId is { } tid)
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tid, ct);
            tenant.IsActive = false; // nothing is deleted during the grace window
        }
    }

    private static Task<TenantEntitlement?> FindEntitlementAsync(
        PlatformDbContext db, string subscriptionRef, CancellationToken ct) =>
        db.TenantEntitlements.FirstOrDefaultAsync(e => e.SubscriptionRef == subscriptionRef, ct);

    /// <summary>
    /// Suspension is the tenant-wide kill switch (Tenant.IsActive, enforced on every request) —
    /// data untouched. Reactivation only revives PastDue/Active states, never a cancellation.
    /// </summary>
    private static async Task SetSuspendedAsync(
        PlatformDbContext db, TenantEntitlement entitlement, bool suspended, CancellationToken ct)
    {
        if (entitlement.Status is EntitlementStatus.Canceled or EntitlementStatus.Deprovisioned)
        {
            return; // a canceled subscription doesn't flip back on a stray invoice event
        }

        entitlement.Status = suspended ? EntitlementStatus.PastDue : EntitlementStatus.Active;
        if (entitlement.TenantId is { } tid)
        {
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tid, ct);
            tenant.IsActive = !suspended;
        }
    }

    /// <summary>
    /// A completed checkout carries OUR provisioning request in the session's metadata (set when
    /// the Checkout Session is created): productId, plan, name, slug, adminEmail — plus optional
    /// adminSubject, modules (comma-separated), seats, monthlyTokenBudget.
    /// </summary>
    private async Task HandleCheckoutCompletedAsync(
        PlatformDbContext db, ITenantProvisioningService provisioning, BillingEvent evt, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(evt.PayloadJson);
        var session = json.RootElement.GetProperty("data").GetProperty("object");
        var subscriptionRef = Str(session, "subscription")
            ?? throw new InvalidOperationException("checkout session has no subscription id.");
        var metadata = session.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
            ? m
            : throw new InvalidOperationException("checkout session has no metadata.");

        // The entitlement is the idempotency anchor: one subscription = one entitlement = one tenant.
        var entitlement = await db.TenantEntitlements
            .FirstOrDefaultAsync(e => e.SubscriptionRef == subscriptionRef, ct);
        if (entitlement is { TenantId: not null })
        {
            return; // fully processed on a previous attempt/delivery
        }

        entitlement ??= db.TenantEntitlements.Add(new TenantEntitlement
        {
            ProductId = Str(metadata, "productId") ?? "unknown",
            Plan = Str(metadata, "plan") ?? "unknown",
            SubscriptionRef = subscriptionRef,
            CustomerRef = Str(session, "customer"),
            Seats = Int(metadata, "seats"),
        }).Entity;

        var result = await provisioning.ProvisionAsync(new ProvisionTenantCommand(
            Name: Str(metadata, "name") ?? "",
            Slug: Str(metadata, "slug") ?? "",
            AdminEmail: Str(metadata, "adminEmail") ?? "",
            AdminSubject: Str(metadata, "adminSubject"),
            Modules: Str(metadata, "modules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxSeats: Int(metadata, "seats"),
            MonthlyTokenBudget: Long(metadata, "monthlyTokenBudget")), ct);

        if (result.Error == ProvisionError.SlugTaken)
        {
            // A retry after the provision landed but before the entitlement update was saved —
            // or a genuine collision. Correlate by slug; fail loudly when it isn't ours.
            var slug = Str(metadata, "slug")?.Trim().ToLowerInvariant();
            var existing = await db.Tenants.FirstAsync(t => t.Slug == slug, ct);
            var claimed = await db.TenantEntitlements.AnyAsync(
                e => e.TenantId == existing.Id && e.SubscriptionRef != subscriptionRef, ct);
            if (claimed)
            {
                throw new InvalidOperationException($"slug '{slug}' is already owned by another subscription.");
            }

            entitlement.TenantId = existing.Id;
        }
        else if (!result.Ok)
        {
            throw new InvalidOperationException($"provisioning failed: {result.ErrorDetail}");
        }
        else
        {
            entitlement.TenantId = result.TenantId;
        }

        entitlement.Status = EntitlementStatus.Active;
        entitlement.UpdatedAt = DateTimeOffset.UtcNow;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Provisioned tenant {TenantId} for subscription {SubscriptionRef} ({Product}/{Plan}).",
                entitlement.TenantId, subscriptionRef, entitlement.ProductId, entitlement.Plan);
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement obj, string name) =>
        int.TryParse(Str(obj, name), out var v) ? v : null;

    private static long? Long(JsonElement obj, string name) =>
        long.TryParse(Str(obj, name), out var v) ? v : null;
}
