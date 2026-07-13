using Plenipo.Modules.Sdk;

namespace Plenipo.Connectors.Sdk;

/// <summary>
/// Supplies a connector's executable tools, built inside the request scope like module tools. The
/// platform appends them to every module's agent — but only when the connector is enabled for the
/// caller's tenant, and each tool remains individually permission-gated before the model sees any
/// schema. Fetch-style tools should set <c>RequiresApproval</c>: they copy external data into the
/// platform.
/// </summary>
public interface IConnectorToolSource
{
    /// <summary>The connector these tools belong to. Must match the connector's manifest id.</summary>
    public string ConnectorId { get; }

    /// <summary>Build the connector's tools using services resolved from the current scope.</summary>
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices);
}

/// <summary>
/// How connector code reads its tenant-level settings (the values the admin entered on the
/// Integrations page, secrets already unprotected). Implemented by the platform; scoped to the
/// current tenant.
/// </summary>
public interface IConnectorSettings
{
    /// <summary>
    /// The connector's settings for the current tenant, or null when the connector is not enabled
    /// here — tools should answer with an honest "not enabled/configured" message, not throw.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>?> GetAsync(string connectorId, CancellationToken cancellationToken = default);
}
