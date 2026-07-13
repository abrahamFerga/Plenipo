using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// One user's OAuth session with a delegated connector (stage 2 of connector enablement: the admin
/// enables per tenant, each user connects their own account). Tokens are stored data-protected;
/// disabling the connector deletes these rows — disable REVOKES, re-enable forces re-auth.
/// </summary>
public sealed class UserConnectorLogin : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required string ConnectorId { get; set; }

    /// <summary>Data-protected JSON: access token, optional refresh token, expiry.</summary>
    public required string ProtectedTokensJson { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
