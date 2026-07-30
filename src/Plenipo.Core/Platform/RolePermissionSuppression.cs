using Plenipo.Core.Entities;

namespace Plenipo.Core.Platform;

/// <summary>
/// A baseline permission a tenant admin has REMOVED from a role.
///
/// Deviation storage, matching <see cref="UserNotificationPreference"/> and TenantModule enablement:
/// no row means the declared baseline applies, so a product can add a permission — or a whole role —
/// with <c>AddPlenipoRole</c> and it reaches every tenant with no backfill and no reconciler. A row
/// exists only where an admin deliberately took something away, and it outlives every later baseline
/// change. That is what keeps a tenant admin's edit authoritative over the product's declaration
/// without freezing the tenant against every future declaration.
/// </summary>
public sealed class RolePermissionSuppression : TenantEntityBase
{
    /// <summary>The role the permission is withheld from. Never <c>system_admin</c>, which is fixed at <c>*</c>.</summary>
    public required string Role { get; set; }

    public required string Permission { get; set; }
}
