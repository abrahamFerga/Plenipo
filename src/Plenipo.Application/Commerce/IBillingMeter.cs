namespace Plenipo.Application.Commerce;

/// <summary>
/// Reports metered usage to the payment provider (Stripe billing meter events) so platform-key
/// AI consumption bills through the subscription. BYO-key tenants are never metered — their
/// traffic runs on their own provider bill.
/// </summary>
public interface IBillingMeter
{
    public bool IsConfigured { get; }

    /// <summary>One usage report; <paramref name="idempotencyKey"/> makes redelivery harmless.</summary>
    public Task ReportUsageAsync(
        string customerRef, long value, DateTimeOffset timestamp, string idempotencyKey,
        CancellationToken cancellationToken = default);
}
