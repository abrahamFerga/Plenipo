using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>Assignment of a system role to a user (Layer 1 of the RBAC model).</summary>
public sealed class UserRole : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public required string Role { get; set; }
}
