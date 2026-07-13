namespace Plenipo.Core.Multitenancy;

/// <summary>
/// Marker for entities that belong to a single tenant. The persistence layer applies a
/// global query filter on <see cref="TenantId"/> to every type implementing this interface,
/// making row-level multi-tenancy the default and impossible to forget.
/// </summary>
public interface ITenantOwned
{
    public Guid TenantId { get; }
}
