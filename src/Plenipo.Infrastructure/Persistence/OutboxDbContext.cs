using Plenipo.Infrastructure.Auditing;
using Plenipo.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Persistence;

/// <summary>
/// A deliberately minimal context over the platform database that maps only the <c>audit_outbox</c> table —
/// and, crucially, carries <b>no audit interceptor</b>. The audit log enqueues deferred records through this
/// context so persisting them can't trigger entity-change auditing (which would recurse) or be audited
/// itself. The table is created by <see cref="PlatformDbContext"/>'s migration; this context never migrates.
/// </summary>
public sealed class OutboxDbContext(DbContextOptions<OutboxDbContext> options) : DbContext(options)
{
    /// <summary>Connection shared with the platform database (same schema as the platform tables).</summary>
    public const string ConnectionName = "plenipo-platform";

    public DbSet<AuditOutboxMessage> Messages => Set<AuditOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(PlatformDbContext.Schema);
        modelBuilder.ApplyConfiguration(new AuditOutboxMessageConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
