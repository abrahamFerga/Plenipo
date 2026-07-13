namespace Plenipo.Application.Modules;

/// <summary>
/// Resolves which installed domain modules are enabled for the <em>current tenant</em>. Enablement is
/// default-on: a module is enabled unless a tenant admin has explicitly disabled it. This is the single
/// source of truth for the per-tenant module check shared by the workspace catalog
/// (<c>GET /api/platform/modules</c>), the admin surface, and the agent runner — so a disabled module is
/// hidden <em>and</em> uninvocable, consistently.
/// </summary>
public interface ITenantModuleStore
{
    /// <summary>The module ids explicitly disabled for the current tenant.</summary>
    public Task<IReadOnlySet<string>> GetDisabledModuleIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>True unless <paramref name="moduleId"/> is explicitly disabled for the current tenant.</summary>
    public Task<bool> IsEnabledAsync(string moduleId, CancellationToken cancellationToken = default);
}
