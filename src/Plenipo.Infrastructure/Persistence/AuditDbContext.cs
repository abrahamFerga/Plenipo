using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Plenipo.Infrastructure.Persistence;

/// <summary>
/// Dedicated append-only store for audit records, kept on a separate database (and ideally a separate
/// credential) from operational data. Nothing here is ever updated or deleted by application code;
/// retention is enforced at the infrastructure layer.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "plenipo-audit";
    public const string Schema = "audit";

    public DbSet<ToolCallAuditEntry> ToolCalls => Set<ToolCallAuditEntry>();
    public DbSet<AuthAuditEntry> AuthEvents => Set<AuthAuditEntry>();
    public DbSet<EntityChangeAuditEntry> EntityChanges => Set<EntityChangeAuditEntry>();
    public DbSet<TokenUsageRecord> TokenUsage => Set<TokenUsageRecord>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ToolCallAuditEntry>(ConfigureToolCall);
        modelBuilder.Entity<AuthAuditEntry>(ConfigureAuthEvent);
        modelBuilder.Entity<EntityChangeAuditEntry>(ConfigureEntityChange);
        modelBuilder.Entity<TokenUsageRecord>(ConfigureTokenUsage);

        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureToolCall(EntityTypeBuilder<ToolCallAuditEntry> b)
    {
        b.ToTable("tool_calls");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ToolName).HasMaxLength(128).IsRequired();
        b.Property(x => x.Permission).HasMaxLength(200).IsRequired();
        b.Property(x => x.ArgumentsJson).HasColumnType("jsonb");
        b.Property(x => x.ResultJson).HasColumnType("jsonb");
        b.Property(x => x.UserDisplay).HasMaxLength(200);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
        b.HasIndex(x => x.ConversationId);
    }

    private static void ConfigureAuthEvent(EntityTypeBuilder<AuthAuditEntry> b)
    {
        b.ToTable("auth_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.UserDisplay).HasMaxLength(200);
        b.Property(x => x.Detail).HasMaxLength(1000);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
    }

    private static void ConfigureEntityChange(EntityTypeBuilder<EntityChangeAuditEntry> b)
    {
        b.ToTable("entity_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(200).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ChangesJson).HasColumnType("jsonb");
        b.Property(x => x.UserDisplay).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
        b.HasIndex(x => new { x.EntityType, x.EntityId });
    }

    private static void ConfigureTokenUsage(EntityTypeBuilder<TokenUsageRecord> b)
    {
        b.ToTable("token_usage");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(32).IsRequired();
        b.Property(x => x.Model).HasMaxLength(128).IsRequired();
        b.Property(x => x.UserDisplay).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.OccurredAt });
        b.HasIndex(x => new { x.TenantId, x.ModuleId });
        b.HasIndex(x => x.ConversationId);
    }

    private void EnsureAppendOnly()
    {
        if (ChangeTracker.Entries().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit records are append-only and cannot be updated or deleted.");
        }
    }
}
