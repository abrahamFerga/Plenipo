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

    /// <summary>
    /// When this tenant's role rows were converted from legacy full-set storage to deviation storage
    /// (see <see cref="RolePermissionSuppression"/>). Tenants created on or after that release are born
    /// converted — the property initializer runs at construction, and EF overwrites it with null only
    /// when materializing a row that predates the column. Nothing reads this on the request path; it
    /// exists solely to make the one-shot startup conversion idempotent across restarts.
    /// </summary>
    public DateTimeOffset? RolePermissionsConvertedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<TenantModule> Modules { get; set; } = [];
    public ICollection<User> Users { get; set; } = [];
}
