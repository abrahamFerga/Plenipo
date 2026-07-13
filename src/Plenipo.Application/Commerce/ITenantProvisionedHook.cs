namespace Plenipo.Application.Commerce;

/// <summary>Everything a product needs to act on a freshly provisioned tenant.</summary>
public sealed record TenantProvisionedContext(
    Guid TenantId,
    string Slug,
    string Name,
    Guid AdminUserId,
    string AdminEmail,
    string AdminSubject,
    IReadOnlyList<string> EnabledModules,
    int? MaxSeats,
    long? MonthlyTokenBudget);

/// <summary>
/// A product's chance to act right after a tenant is provisioned — whoever triggered it (the
/// operator endpoint or the billing worker): send the welcome email, seed domain data, register
/// the org in an external system. Hosts register any number
/// (<c>services.AddPlenipoTenantProvisionedHook&lt;T&gt;()</c>). Hooks run AFTER the provisioning
/// transaction committed — the tenant exists whatever a hook does. A hook failure is logged and
/// never rolls the tenant back, so hooks must be idempotent and safe to re-run by hand.
/// </summary>
public interface ITenantProvisionedHook
{
    public Task OnTenantProvisionedAsync(TenantProvisionedContext context, CancellationToken cancellationToken = default);
}
