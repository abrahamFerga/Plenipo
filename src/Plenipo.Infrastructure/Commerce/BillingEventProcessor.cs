using System.Text.Json;
using Plenipo.Application.Commerce;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Commerce;

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
        var dedicated = scope.ServiceProvider.GetRequiredService<IDedicatedEnvironmentProvisioner>();
        var offerings = scope.ServiceProvider.GetRequiredService<IProductOfferingCatalog>();

        await SweepExpiredGraceAsync(db, dedicated, offerings, ct);
        await ExportUsageAsync(scope.ServiceProvider, db, ct);

        var pending = await db.BillingEvents
            .Where(e => e.ProcessedAt == null && e.Attempts < MaxAttempts)
            .OrderBy(e => e.ReceivedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var evt in pending)
        {
            try
            {
                await ProcessAsync(db, provisioning, dedicated, offerings, evt, ct);
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
        PlatformDbContext db, ITenantProvisioningService provisioning,
        IDedicatedEnvironmentProvisioner dedicated, IProductOfferingCatalog offerings,
        BillingEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case "checkout.session.completed":
                await HandleCheckoutCompletedAsync(db, provisioning, dedicated, offerings, evt, ct);
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

    /// <summary>
    /// The cancellation grace window's other end: entitlements whose DeprovisionAfter has passed
    /// get their dedicated environment destroyed (workflow dispatch) and move to Deprovisioned.
    /// Shared-SaaS tenants stay suspended-and-kept — row deletion is an operator decision, never
    /// automatic.
    /// </summary>
    private async Task SweepExpiredGraceAsync(
        PlatformDbContext db, IDedicatedEnvironmentProvisioner dedicated, IProductOfferingCatalog offerings,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.TenantEntitlements
            .Where(e => e.Status == EntitlementStatus.Canceled && e.DeprovisionAfter != null && e.DeprovisionAfter < now)
            .Take(10)
            .ToListAsync(ct);

        foreach (var entitlement in expired)
        {
            var isDedicated = offerings.FindPlan(entitlement.ProductId, entitlement.Plan)?.Dedicated
                ?? string.Equals(entitlement.Plan, "dedicated", StringComparison.OrdinalIgnoreCase);
            if (isDedicated && dedicated.IsConfigured)
            {
                if (entitlement.CustomerSlug is { } slug)
                {
                    await dedicated.DispatchAsync(new DedicatedEnvironmentRequest(slug, "destroy"), ct);
                }
                else
                {
                    logger.LogError(
                        "Entitlement {Id} (dedicated) has no customer slug — environment must be destroyed by hand.",
                        entitlement.Id);
                }
            }

            entitlement.Status = EntitlementStatus.Deprovisioned;
            entitlement.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }
    }

    private DateTimeOffset _lastUsageExport = DateTimeOffset.MinValue;

    /// <summary>
    /// The metering half of "platform-managed AI" (docs/COMMERCIALIZATION.md phase 7): pushes
    /// each Active, platform-key tenant's token consumption since its watermark to the billing
    /// meter. BYO-key tenants (their own provider connection) are never metered. The watermark
    /// advances only after a successful report; the meter's idempotency identifier makes a
    /// crash-between-report-and-save retry harmless.
    /// </summary>
    private async Task ExportUsageAsync(IServiceProvider services, PlatformDbContext db, CancellationToken ct)
    {
        var meter = services.GetRequiredService<IBillingMeter>();
        var now = DateTimeOffset.UtcNow;
        if (!meter.IsConfigured || now - _lastUsageExport < TimeSpan.FromSeconds(options.Value.UsageExportSeconds))
        {
            return;
        }

        _lastUsageExport = now;
        var audit = services.GetRequiredService<AuditDbContext>();

        var active = await db.TenantEntitlements
            .Where(e => e.Status == EntitlementStatus.Active && e.TenantId != null && e.CustomerRef != null)
            .Take(100)
            .ToListAsync(ct);

        foreach (var entitlement in active)
        {
            // BYO key = the tenant overrode the provider connection; their usage is their bill.
            var byok = await db.TenantAiSettings.IgnoreQueryFilters()
                .AnyAsync(a => a.TenantId == entitlement.TenantId && a.Provider != null && a.Provider != "", ct);
            if (byok)
            {
                continue;
            }

            var since = entitlement.UsageReportedThrough ?? entitlement.CreatedAt;
            var total = await audit.TokenUsage
                .Where(u => u.TenantId == entitlement.TenantId && u.OccurredAt > since && u.OccurredAt <= now)
                .SumAsync(u => (long?)u.TotalTokens, ct) ?? 0L;
            if (total <= 0)
            {
                continue;
            }

            try
            {
                await meter.ReportUsageAsync(
                    entitlement.CustomerRef!, total, now,
                    idempotencyKey: $"{entitlement.Id:N}-{now.ToUnixTimeSeconds()}", ct);
                entitlement.UsageReportedThrough = now;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Watermark untouched — the window re-reports next cycle under a new identifier.
                logger.LogWarning(ex, "Usage export for entitlement {Id} failed; will retry.", entitlement.Id);
            }
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
        PlatformDbContext db, ITenantProvisioningService provisioning,
        IDedicatedEnvironmentProvisioner dedicated, IProductOfferingCatalog offerings,
        BillingEvent evt, CancellationToken ct)
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
            CustomerSlug = Str(metadata, "slug")?.Trim().ToLowerInvariant(),
            Seats = Int(metadata, "seats"),
        }).Entity;

        // The plan the HOST declared is authoritative for what this purchase grants; the checkout
        // metadata only identifies who bought what. Hosts without a registered offering fall back
        // to metadata-driven provisioning (plan == null).
        var plan = offerings.FindPlan(entitlement.ProductId, entitlement.Plan);

        // Dedicated tier: infrastructure, not a row in the shared deployment. Dispatch the
        // deploy-customer workflow and stay Provisioning until the environment reports healthy —
        // a purchase with no dispatcher configured fails loudly (retry → dead-letter → triage).
        if (plan?.Dedicated ?? string.Equals(entitlement.Plan, "dedicated", StringComparison.OrdinalIgnoreCase))
        {
            if (!dedicated.IsConfigured)
            {
                throw new InvalidOperationException(
                    "A dedicated-tier subscription landed but Commerce:Dedicated is not configured.");
            }

            await dedicated.DispatchAsync(new DedicatedEnvironmentRequest(
                Customer: Str(metadata, "slug") ?? throw new InvalidOperationException("dedicated checkout has no slug."),
                Action: "apply",
                Region: Str(metadata, "region"),
                Size: Str(metadata, "size")), ct);
            entitlement.UpdatedAt = DateTimeOffset.UtcNow;
            return; // Status stays Provisioning; the environment's health, not the dispatch, makes it Active
        }

        var result = await provisioning.ProvisionAsync(new ProvisionTenantCommand(
            Name: Str(metadata, "name") ?? "",
            Slug: Str(metadata, "slug") ?? "",
            AdminEmail: Str(metadata, "adminEmail") ?? "",
            AdminSubject: Str(metadata, "adminSubject"),
            Modules: plan is not null
                ? plan.Modules
                : Str(metadata, "modules")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxSeats: Int(metadata, "seats") ?? plan?.DefaultSeats,
            MonthlyTokenBudget: plan is not null ? plan.MonthlyTokenBudget : Long(metadata, "monthlyTokenBudget")), ct);

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
