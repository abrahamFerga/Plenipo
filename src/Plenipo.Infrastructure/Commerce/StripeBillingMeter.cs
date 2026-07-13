using Plenipo.Application.Commerce;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Commerce;

/// <summary>
/// Reports usage to Stripe's billing meter events API (form-encoded POST /v1/billing/meter_events;
/// the <c>identifier</c> is Stripe's idempotency handle, so a retried export never double-bills).
/// One small REST call — no SDK needed for this surface.
/// </summary>
public sealed class StripeBillingMeter(
    IHttpClientFactory httpClients,
    IOptions<CommerceOptions> options,
    ILogger<StripeBillingMeter> logger) : IBillingMeter
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.StripeApiKey);

    public async Task ReportUsageAsync(
        string customerRef, long value, DateTimeOffset timestamp, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var client = httpClients.CreateClient(nameof(StripeBillingMeter));
        client.BaseAddress ??= new Uri("https://api.stripe.com/");
        client.DefaultRequestHeaders.Authorization = new("Bearer", opts.StripeApiKey);

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["event_name"] = opts.MeterEventName,
            ["identifier"] = idempotencyKey,
            ["timestamp"] = timestamp.ToUnixTimeSeconds().ToString(),
            ["payload[stripe_customer_id]"] = customerRef,
            ["payload[value]"] = value.ToString(),
        });
        using var response = await client.PostAsync("v1/billing/meter_events", body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"meter event failed: {(int)response.StatusCode} {detail}");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Reported {Value} tokens for {CustomerRef} to meter {Meter}.",
                value, customerRef, opts.MeterEventName);
        }
    }
}
