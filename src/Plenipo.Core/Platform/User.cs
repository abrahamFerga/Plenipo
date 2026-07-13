using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>A user within a tenant, provisioned just-in-time from the external identity provider.</summary>
public sealed class User : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>External identity provider subject (OIDC <c>sub</c>). Unique per tenant.</summary>
    public required string Subject { get; set; }

    public required string Email { get; set; }
    public string? DisplayName { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>System roles assigned to this user (Layer 1 of RBAC).</summary>
    public ICollection<UserRole> Roles { get; set; } = [];

    /// <summary>Direct per-tenant permission grants (Layer 2 of RBAC).</summary>
    public ICollection<UserPermission> Permissions { get; set; } = [];
}
