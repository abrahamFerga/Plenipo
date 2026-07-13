namespace Plenipo.Application.Commerce;

/// <summary>What the billing worker asks for when a Dedicated-tier subscription needs infrastructure.</summary>
public sealed record DedicatedEnvironmentRequest(
    /// <summary>Customer slug — the Terraform state key and resource-name segment.</summary>
    string Customer,
    /// <summary>"apply" provisions/updates; "destroy" tears down after the cancellation grace.</summary>
    string Action,
    string? Region = null,
    string? Size = null);

/// <summary>
/// Dispatches dedicated-environment work (docs/COMMERCIALIZATION.md phase 6). The default
/// implementation fires the repo's deploy-customer GitHub Actions workflow; tests substitute a
/// recorder. Dispatch is fire-and-forget from the worker's perspective — the entitlement stays
/// Provisioning until the environment reports healthy (operator-observable via the workflow run).
/// </summary>
public interface IDedicatedEnvironmentProvisioner
{
    /// <summary>True when a dispatcher is configured — a Dedicated purchase without one must fail loudly, not silently.</summary>
    public bool IsConfigured { get; }

    public Task DispatchAsync(DedicatedEnvironmentRequest request, CancellationToken cancellationToken = default);
}
