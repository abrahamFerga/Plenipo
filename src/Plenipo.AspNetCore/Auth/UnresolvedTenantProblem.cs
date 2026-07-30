using Plenipo.Infrastructure.Context;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// One wording for "your tenant did not resolve", shared by <c>/api/platform/me</c>, the body of a 403,
/// and the SignalR hub — so a client hears the same cause whichever surface it hits first.
///
/// <para>Deliberately narrow. It never changes a status code or an authorization decision: the request was
/// already going to fail on an empty permission set, and this only replaces silence with a reason. It also
/// never reveals how many tenants the deployment has — only the slug the caller themselves supplied.</para>
/// </summary>
public static class UnresolvedTenantProblem
{
    public const string Title = "Tenant not resolved";

    /// <summary>The cause, or null when a tenant resolved normally.</summary>
    public static string? Describe(RequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Resolution switch
        {
            TenantResolution.ClaimDidNotMatch =>
                $"No tenant matches '{context.RequestedTenant}'. The tenant claim on your token must match a tenant " +
                "slug in this deployment. If this is a new deployment, its first tenant is created from the " +
                "Bootstrap configuration section — see docs/CONFIGURATION.md.",

            TenantResolution.NoClaimAndAmbiguous =>
                "Your token names no tenant, and this deployment does not have exactly one to fall back to. " +
                "Configure Auth:TenantClaim and have the identity provider assert the tenant slug. If this is a " +
                "new deployment with no tenants at all, create the first one from the Bootstrap configuration " +
                "section — see docs/CONFIGURATION.md.",

            _ => null,
        };
    }
}
