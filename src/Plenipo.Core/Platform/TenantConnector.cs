using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// A tenant's state for one installed connector. Connectors are default-OFF (they reach outside the
/// platform boundary), so a row with <see cref="Enabled"/> is what turns a connector's tools on for
/// a tenant; no row means disabled. Settings are the admin-entered values for the connector's
/// declared schema, with secret fields protected at rest.
/// </summary>
public sealed class TenantConnector : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The connector's manifest id (e.g. "azure-blob").</summary>
    public required string ConnectorId { get; set; }

    public bool Enabled { get; set; }

    /// <summary>
    /// JSON object of setting key → value. Values for settings the manifest marks secret are stored
    /// data-protected and never leave the server unredacted.
    /// </summary>
    public string? SettingsJson { get; set; }
}
