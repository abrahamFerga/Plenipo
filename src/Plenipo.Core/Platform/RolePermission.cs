using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// A permission a <em>role</em> grants within a tenant — the configurable form of the role → permission
/// baseline (Layer 1 → Layer 2 of the RBAC model). Seeded from the platform defaults when a tenant is
/// created, then editable by a tenant admin, so the meaning of a role can be tuned per tenant without a
/// code change. The <c>system_admin</c> role is intentionally not editable (it always holds <c>*</c>).
/// </summary>
public sealed class RolePermission : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The role this grant applies to (one of the platform's system roles).</summary>
    public required string Role { get; set; }

    /// <summary>Permission string, possibly a wildcard such as <c>tools.finance.*</c> or <c>platform.*</c>.</summary>
    public required string Permission { get; set; }
}
