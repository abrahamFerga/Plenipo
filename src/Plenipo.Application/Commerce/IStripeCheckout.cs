namespace Plenipo.Application.Commerce;

/// <summary>What the public checkout endpoint asks the payment provider to open.</summary>
public sealed record CheckoutSessionRequest(
    string PriceId,
    int Quantity,
    IReadOnlyDictionary<string, string> Metadata,
    string SuccessUrl,
    string CancelUrl);

/// <summary>
/// Creates hosted Checkout Sessions. The session's METADATA carries the provisioning identity the
/// webhook worker later consumes — set server-side here, never by the browser. Tests substitute a
/// recorder; the real implementation is one Stripe REST call.
/// </summary>
public interface IStripeCheckout
{
    public bool IsConfigured { get; }

    /// <summary>Returns the hosted checkout URL the buyer is redirected to.</summary>
    public Task<string> CreateSessionAsync(CheckoutSessionRequest request, CancellationToken cancellationToken = default);
}
