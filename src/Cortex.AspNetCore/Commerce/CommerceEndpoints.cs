using System.Text.Json;
using Cortex.Application.Commerce;
using Cortex.AspNetCore.RateLimiting;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cortex.AspNetCore.Commerce;

/// <summary>
/// The billing webhook: receive fast, process safe (docs/COMMERCIALIZATION.md). Verify the
/// signature against the RAW body, persist the event by its provider id, return 200 — a worker
/// processes the inbox asynchronously and idempotently. Duplicate deliveries are 200s (Stripe
/// retries until acknowledged); a bad signature is a 400; a deployment with commerce off has no
/// surface here at all (404).
/// </summary>
public static class CommerceEndpoints
{
    public static void MapCommerceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/stripe", async (
                HttpContext http, IOptions<CommerceOptions> options, PlatformDbContext db,
                CancellationToken cancellationToken) =>
            {
                var opts = options.Value;
                if (!opts.IsEnabled)
                {
                    return Results.NotFound();
                }

                using var buffer = new MemoryStream();
                await http.Request.Body.CopyToAsync(buffer, cancellationToken);
                var body = buffer.ToArray();

                if (!StripeSignature.IsValid(
                        body, http.Request.Headers["Stripe-Signature"], opts.WebhookSecret!,
                        DateTimeOffset.UtcNow, opts.SignatureToleranceSeconds))
                {
                    return Results.BadRequest();
                }

                string eventId, type;
                try
                {
                    using var json = JsonDocument.Parse(body);
                    eventId = json.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
                    type = json.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                }
                catch (JsonException)
                {
                    return Results.BadRequest();
                }

                if (eventId.Length == 0)
                {
                    return Results.BadRequest();
                }

                // Redelivery of an event we already hold is success — never make Stripe retry it.
                if (!await db.BillingEvents.AnyAsync(e => e.Provider == "stripe" && e.EventId == eventId, cancellationToken))
                {
                    db.BillingEvents.Add(new BillingEvent
                    {
                        Provider = "stripe",
                        EventId = eventId,
                        Type = type,
                        PayloadJson = System.Text.Encoding.UTF8.GetString(body),
                    });

                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateException)
                    {
                        // A concurrent delivery of the same event won the unique index — same outcome.
                    }
                }

                return Results.Ok();
            })
            .AllowAnonymous() // the signature IS the authentication
            .WithTags("Commerce")
            .WithName("Commerce_StripeWebhook");

        // The purchase's front door: the pricing page POSTs here and redirects to the returned
        // hosted-checkout URL. Anonymous by nature (the buyer isn't a user yet) — validated
        // against the PRODUCT OFFERING (the server's truth, never the browser's), rate-limited
        // per IP, and the provisioning identity goes into the session metadata server-side.
        app.MapPost("/api/commerce/checkout", async (
                StartCheckoutRequest body, IOptions<CommerceOptions> options,
                IProductOfferingCatalog offerings, IStripeCheckout checkout, PlatformDbContext db,
                CancellationToken cancellationToken) =>
            {
                var opts = options.Value;
                if (!opts.IsEnabled || !checkout.IsConfigured)
                {
                    return Results.NotFound();
                }

                var productId = body.ProductId?.Trim() ?? "";
                var planId = body.Plan?.Trim() ?? "";
                var name = body.OrgName?.Trim() ?? "";
                var slug = body.Slug?.Trim().ToLowerInvariant() ?? "";
                var adminEmail = body.AdminEmail?.Trim() ?? "";

                var plan = offerings.FindPlan(productId, planId);
                if (plan is null)
                {
                    return Results.BadRequest("Unknown product or plan.");
                }

                var priceId = opts.Prices.GetValueOrDefault(productId)?.GetValueOrDefault(planId);
                if (string.IsNullOrWhiteSpace(priceId))
                {
                    return Results.BadRequest("This plan is not purchasable yet (no price configured).");
                }

                if (name.Length is 0 or > 120 || adminEmail.Length is 0 or > 254 || !adminEmail.Contains('@'))
                {
                    return Results.BadRequest("orgName and a valid adminEmail are required.");
                }

                if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z][a-z0-9-]{1,20}$"))
                {
                    return Results.BadRequest("slug must be lowercase kebab, 2-21 chars, starting with a letter.");
                }

                var seats = body.Seats ?? plan.DefaultSeats ?? 1;
                if (seats < 1 || seats > 500)
                {
                    return Results.BadRequest("seats must be between 1 and 500.");
                }

                // Advisory availability check (final authority stays at provisioning time).
                var taken = await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, cancellationToken)
                    || await db.TenantEntitlements.AnyAsync(e => e.CustomerSlug == slug, cancellationToken);
                if (taken)
                {
                    return Results.Conflict($"The workspace name '{slug}' is already taken.");
                }

                var url = await checkout.CreateSessionAsync(new CheckoutSessionRequest(
                    PriceId: priceId,
                    Quantity: seats,
                    Metadata: new Dictionary<string, string>
                    {
                        ["productId"] = productId,
                        ["plan"] = planId,
                        ["name"] = name,
                        ["slug"] = slug,
                        ["adminEmail"] = adminEmail,
                        ["seats"] = seats.ToString(),
                    },
                    SuccessUrl: opts.CheckoutSuccessUrl ?? "https://example.invalid/welcome",
                    CancelUrl: opts.CheckoutCancelUrl ?? "https://example.invalid/pricing"), cancellationToken);

                return Results.Ok(new { url });
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingSetup.PublicPolicy)
            .WithTags("Commerce")
            .WithName("Commerce_StartCheckout");
    }

    /// <summary>What the pricing page sends to open a checkout.</summary>
    public sealed record StartCheckoutRequest(
        string? ProductId, string? Plan, string? OrgName, string? Slug, string? AdminEmail, int? Seats);
}
