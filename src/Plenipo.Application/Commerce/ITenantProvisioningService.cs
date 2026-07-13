namespace Plenipo.Application.Commerce;

/// <summary>Everything a subscription needs provisioned, in one command (docs/COMMERCIALIZATION.md).</summary>
public sealed record ProvisionTenantCommand(
    string Name,
    string Slug,
    string AdminEmail,
    string? AdminSubject = null,
    string? AdminDisplayName = null,
    IReadOnlyList<string>? Modules = null,
    int? MaxSeats = null,
    long? MonthlyTokenBudget = null);

public enum ProvisionError
{
    None = 0,
    Invalid = 1,
    SlugTaken = 2,
}

public sealed record ProvisionResult(
    ProvisionError Error,
    string? ErrorDetail = null,
    Guid TenantId = default,
    Guid AdminUserId = default,
    string? AdminSubject = null,
    IReadOnlyList<string>? EnabledModules = null)
{
    public bool Ok => Error == ProvisionError.None;
}

/// <summary>
/// The one-transaction provisioning orchestrator: tenant + first admin (tenant_admin) + licensed
/// modules + metered AI budget + seat limit. Shared by the operator endpoint
/// (POST /api/admin/tenants/provision) and the billing worker — one code path, whoever triggers.
/// </summary>
public interface ITenantProvisioningService
{
    public Task<ProvisionResult> ProvisionAsync(ProvisionTenantCommand command, CancellationToken cancellationToken = default);
}
