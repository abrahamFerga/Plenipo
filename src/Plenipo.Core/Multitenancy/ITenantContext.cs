namespace Plenipo.Core.Multitenancy;

/// <summary>
/// Ambient information about the tenant the current operation belongs to.
/// Resolved per request from the authenticated principal and consumed by the
/// persistence layer's global query filters so no query can cross a tenant boundary.
/// </summary>
public interface ITenantContext
{
    /// <summary>The active tenant, or <c>null</c> for unauthenticated / platform-level calls.</summary>
    public Guid? TenantId { get; }

    /// <summary>True when a tenant has been resolved for the current operation.</summary>
    public bool HasTenant { get; }

    /// <summary>The resolved tenant id, throwing if none is present. Use inside tenant-scoped code paths.</summary>
    public Guid RequireTenantId();
}
