using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;

namespace Plenipo.Application.Connectors;

/// <summary>
/// The installed connectors' manifests — the connector counterpart of <c>IModuleCatalog</c>.
/// Validated once at startup (unique, well-formed ids).
/// </summary>
public interface IConnectorCatalog
{
    public IReadOnlyList<ConnectorManifest> Manifests { get; }

    public bool TryGetManifest(string connectorId, out ConnectorManifest? manifest);
}

/// <summary>
/// Resolves which installed connectors are enabled for the <em>current tenant</em>. Enablement is
/// default-OFF — the inverse of modules — because a connector reaches outside the platform
/// boundary: a tenant admin must explicitly enable it (an audited act) before its tools exist for
/// that tenant.
/// </summary>
public interface ITenantConnectorStore
{
    /// <summary>The connector ids explicitly enabled for the current tenant.</summary>
    public Task<IReadOnlySet<string>> GetEnabledConnectorIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>True only when a tenant admin has enabled <paramref name="connectorId"/> here.</summary>
    public Task<bool> IsEnabledAsync(string connectorId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The agent runner's view of connector tools: everything contributed by connectors that are
/// enabled for the current tenant (async because enablement lives in the database). A disabled
/// connector's tools are never built — the model cannot see a tool the tenant hasn't turned on.
/// </summary>
public interface IConnectorToolCatalog
{
    public Task<IReadOnlyList<ModuleTool>> GetEnabledToolsAsync(
        IServiceProvider scopedServices, CancellationToken cancellationToken = default);
}
