using Plenipo.Core.Entities;

namespace Plenipo.Core.Platform;

/// <summary>An isolation boundary: an organization using the platform. Root of all tenant-owned data.</summary>
public sealed class Tenant : EntityBase
{
    public required string Name { get; set; }

    /// <summary>URL-safe unique key used in routing / invitations.</summary>
    public required string Slug { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Seat limit from the tenant's subscription (enforced at user creation/JIT provisioning).
    /// Null = unlimited (operator-created tenants, dedicated deployments).
    /// </summary>
    public int? MaxSeats { get; set; }

    public ICollection<TenantModule> Modules { get; set; } = [];
    public ICollection<User> Users { get; set; } = [];
}
