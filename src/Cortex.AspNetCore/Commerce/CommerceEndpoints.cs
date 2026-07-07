using System.Text.Json;
using Cortex.Application.Commerce;
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
    }
}
