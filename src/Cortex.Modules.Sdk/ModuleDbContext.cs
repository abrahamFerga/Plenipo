using Cortex.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Sdk;

/// <summary>
/// The base <see cref="DbContext"/> for module persistence. Its one job: stamp
/// <see cref="EntityBase.CreatedAt"/> / <see cref="EntityBase.UpdatedAt"/> on every save path.
/// The platform's audit interceptor covers only the platform's own context — a module context
/// deriving straight from <see cref="DbContext"/> silently persists
/// <c>default(DateTimeOffset)</c> timestamps, which breaks everything ordered or filtered by
/// recency (activity feeds, "most recent" lookups, retention sweeps) without failing a single
/// write. Deriving from this class makes that bug unrepresentable.
/// </summary>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditFields()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            switch (entry.State)
            {
                // A caller that stamped its own CreatedAt (imports carrying source timestamps) wins.
                case EntityState.Added when entry.Entity.CreatedAt == default:
                    entry.Entity.CreatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    break;
            }
        }
    }
}
