using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Entities;

/// <summary>
/// Base for tenant-owned entities. Implements <see cref="ITenantOwned"/> so the global query
/// filter is applied automatically, and carries audit columns. Most domain entities — and every
/// entity inside a module — derive from this.
/// </summary>
public abstract class TenantEntityBase : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }
}
