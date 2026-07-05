using System.Linq.Expressions;
using System.Reflection;
using Cortex.Core.Multitenancy;
using Cortex.Core.Platform;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Infrastructure.Persistence;

/// <summary>
/// Operational database for the platform. Every <see cref="ITenantOwned"/> entity gets a global query
/// filter on the ambient tenant, so no query — including ones written by module code — can read or
/// write across a tenant boundary.
/// </summary>
public sealed class PlatformDbContext(
    DbContextOptions<PlatformDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    public const string ConnectionName = "cortex-platform";
    public const string Schema = "platform";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<TenantModule> TenantModules => Set<TenantModule>();
    public DbSet<TenantAiSettings> TenantAiSettings => Set<TenantAiSettings>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<InstructionSnapshot> InstructionSnapshots => Set<InstructionSnapshot>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<PendingApproval> PendingApprovals => Set<PendingApproval>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<RagCollection> RagCollections => Set<RagCollection>();
    public DbSet<RagChunk> RagChunks => Set<RagChunk>();
    public DbSet<TenantConnector> TenantConnectors => Set<TenantConnector>();
    public DbSet<ConnectorBinding> ConnectorBindings => Set<ConnectorBinding>();
    public DbSet<UserConnectorLogin> UserConnectorLogins => Set<UserConnectorLogin>();

    private static readonly MethodInfo ApplyTenantFilterMethod = typeof(PlatformDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }

            // Every entity carries a client-generated Guid v7 key (set in EntityBase). Tell EF the key is
            // never store-generated, so adding a new entity through an already-tracked navigation graph is
            // correctly detected as an INSERT rather than mistaken for an UPDATE of an existing row.
            var key = entityType.FindPrimaryKey();
            if (key?.Properties is [{ ClrType: var clr, Name: "Id" } idProperty] && clr == typeof(Guid))
            {
                idProperty.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }

        base.OnModelCreating(modelBuilder);
    }

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned
    {
        // Closure over the scoped tenant context — EF re-reads TenantId on every query.
        Expression<Func<TEntity, bool>> filter = e => e.TenantId == tenantContext.TenantId;
        modelBuilder.Entity<TEntity>().HasQueryFilter(filter);
    }
}
