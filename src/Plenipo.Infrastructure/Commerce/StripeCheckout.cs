using System.Text.Json;
using Plenipo.Application.Commerce;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Commerce;

/// <summary>
/// Creates Stripe Checkout Sessions (form-encoded POST /v1/checkout/sessions, mode=subscription).
/// The metadata rides the session and comes back on checkout.session.completed — the worker's
/// provisioning identity. Uses the same secret API key as the meter (Commerce:StripeApiKey).
/// </summary>
public sealed class StripeCheckout(
    IHttpClientFactory httpClients,
    IOptions<CommerceOptions> options) : IStripeCheckout
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.StripeApiKey);

    public async Task<string> CreateSessionAsync(
        CheckoutSessionRequest request, CancellationToken cancellationToken = default)
    {
        var client = httpClients.CreateClient(nameof(StripeCheckout));
        client.BaseAddress ??= new Uri("https://api.stripe.com/");
        client.DefaultRequestHeaders.Authorization = new("Bearer", options.Value.StripeApiKey);

        var form = new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["line_items[0][price]"] = request.PriceId,
            ["line_items[0][quantity]"] = request.Quantity.ToString(),
            ["success_url"] = request.SuccessUrl,
            ["cancel_url"] = request.CancelUrl,
        };
        foreach (var (key, value) in request.Metadata)
        {
            form[$"metadata[{key}]"] = value;
        }

        using var body = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync("v1/checkout/sessions", body, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"checkout session failed: {(int)response.StatusCode} {payload}");
        }

        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("checkout session response had no url.");
    }
}
