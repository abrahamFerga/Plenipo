using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Application.Commerce;

/// <summary>
/// One purchasable plan of a product. The plan — not the checkout metadata — is authoritative
/// for what a purchase grants: metadata identifies WHO bought WHAT (product, plan, org, admin),
/// the plan decides what that means (modules, budget, dedicated infrastructure). Seats stay
/// purchase-time (the subscription quantity), defaulted here.
/// </summary>
public sealed record ProductPlan
{
    /// <summary>The plan key checkout metadata references (e.g. "solo", "team", "dedicated").</summary>
    public required string Id { get; init; }

    /// <summary>Modules this plan licenses. Null = every module the product host installs.</summary>
    public IReadOnlyList<string>? Modules { get; init; }

    /// <summary>Seats when the purchase doesn't specify a quantity. Null = unlimited.</summary>
    public int? DefaultSeats { get; init; }

    /// <summary>Metered AI allowance (platform-managed key). Null = BYO key, no platform budget.</summary>
    public long? MonthlyTokenBudget { get; init; }

    /// <summary>True = this plan provisions a dedicated environment instead of a shared tenant.</summary>
    public bool Dedicated { get; init; }
}

/// <summary>
/// What a product sells (docs/COMMERCIALIZATION.md phase 9). Each product is its own system on
/// the platform — its HOST declares the offering at startup
/// (<see cref="ProductOfferingRegistration.AddCortexProduct"/>), the same way it installs its
/// modules. Peer-to-peer products declare and provision independently.
/// </summary>
public sealed record ProductOffering
{
    /// <summary>The product id checkout metadata references (e.g. "the-lawyer").</summary>
    public required string ProductId { get; init; }

    public required IReadOnlyList<ProductPlan> Plans { get; init; }
}

/// <summary>Lookup over every offering the host registered.</summary>
public interface IProductOfferingCatalog
{
    public ProductPlan? FindPlan(string productId, string planId);
}

public sealed class ProductOfferingCatalog(IEnumerable<ProductOffering> offerings) : IProductOfferingCatalog
{
    private readonly Dictionary<(string, string), ProductPlan> _plans = offerings
        .SelectMany(o => o.Plans.Select(p => (Key: (o.ProductId, p.Id), Plan: p)))
        .ToDictionary(x => x.Key, x => x.Plan);

    public ProductPlan? FindPlan(string productId, string planId) =>
        _plans.GetValueOrDefault((productId, planId));
}

public static class ProductOfferingRegistration
{
    /// <summary>Declares what this product host sells — one call next to its module installs.</summary>
    public static IServiceCollection AddCortexProduct(this IServiceCollection services, ProductOffering offering)
    {
        services.AddSingleton(offering);
        return services;
    }

    /// <summary>Runs after every successful tenant provisioning (see <see cref="ITenantProvisionedHook"/>).</summary>
    public static IServiceCollection AddCortexTenantProvisionedHook<THook>(this IServiceCollection services)
        where THook : class, ITenantProvisionedHook
    {
        services.AddScoped<ITenantProvisionedHook, THook>();
        return services;
    }
}
