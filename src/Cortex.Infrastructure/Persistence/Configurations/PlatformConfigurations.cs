using Cortex.Application.Ai;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cortex.Infrastructure.Persistence.Configurations;

/// <summary>
/// The durable audit outbox lives in the platform schema (the operational DB, which is up when the audit DB
/// may be down). PlatformDbContext owns its migration via assembly scan; only OutboxDbContext — which has no
/// audit interceptor — ever reads or writes it, so enqueuing a deferred audit record can't recurse into the
/// interceptor or audit itself.
/// </summary>
internal sealed class AuditOutboxMessageConfiguration : IEntityTypeConfiguration<AuditOutboxMessage>
{
    public void Configure(EntityTypeBuilder<AuditOutboxMessage> b)
    {
        b.ToTable("audit_outbox");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        b.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("tenants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.HasMany(x => x.Users).WithOne().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Modules).WithOne().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.Subject }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Email });
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.UserId, x.Role }).IsUnique();
    }
}

internal sealed class UserPermissionConfiguration : IEntityTypeConfiguration<UserPermission>
{
    public void Configure(EntityTypeBuilder<UserPermission> b)
    {
        b.ToTable("user_permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Permission).HasMaxLength(200).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.UserId, x.Permission }).IsUnique();
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(64).IsRequired();
        b.Property(x => x.Permission).HasMaxLength(200).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Role, x.Permission }).IsUnique();
    }
}

internal sealed class TenantModuleConfiguration : IEntityTypeConfiguration<TenantModule>
{
    public void Configure(EntityTypeBuilder<TenantModule> b)
    {
        b.ToTable("tenant_modules");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ModuleId }).IsUnique();
    }
}

internal sealed class TenantAiSettingsConfiguration : IEntityTypeConfiguration<TenantAiSettings>
{
    public void Configure(EntityTypeBuilder<TenantAiSettings> b)
    {
        b.ToTable("tenant_ai_settings");
        b.HasKey(x => x.Id);
        b.Property(x => x.SystemPrompt).HasMaxLength(TenantAiSettingsValidator.MaxSystemPromptLength);
        b.HasIndex(x => x.TenantId).IsUnique();
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300);
        // Deliberately text, not jsonb: this is MAF's opaque serialized AgentSession, and jsonb
        // re-orders object keys — which breaks System.Text.Json's polymorphic $type metadata on
        // rehydration. text preserves the framework's serialization byte-for-byte.
        b.Property(x => x.SessionState).HasColumnType("text");
        b.HasIndex(x => new { x.TenantId, x.UserId, x.ModuleId });
        b.HasMany(x => x.Messages).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
    public void Configure(EntityTypeBuilder<ConversationMessage> b)
    {
        b.ToTable("conversation_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.ConversationId);
    }
}

internal sealed class StoredFileConfiguration : IEntityTypeConfiguration<StoredFile>
{
    public void Configure(EntityTypeBuilder<StoredFile> b)
    {
        b.ToTable("stored_files");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(300).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(200).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.Source).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.UserId });
    }
}

internal sealed class BackgroundJobConfiguration : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> b)
    {
        b.ToTable("background_jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(128).IsRequired();
        b.Property(x => x.ArgumentsJson).IsRequired();
        b.Property(x => x.PermissionsSnapshotJson).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ProgressNote).HasMaxLength(500);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.HasIndex(x => x.Status); // the processor's claim scan
        b.HasIndex(x => new { x.TenantId, x.UserId });
    }
}

internal sealed class TenantConnectorConfiguration : IEntityTypeConfiguration<TenantConnector>
{
    public void Configure(EntityTypeBuilder<TenantConnector> b)
    {
        b.ToTable("tenant_connectors");
        b.HasKey(x => x.Id);
        b.Property(x => x.ConnectorId).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ConnectorId }).IsUnique();
    }
}

internal sealed class UserConnectorLoginConfiguration : IEntityTypeConfiguration<UserConnectorLogin>
{
    public void Configure(EntityTypeBuilder<UserConnectorLogin> b)
    {
        b.ToTable("user_connector_logins");
        b.HasKey(x => x.Id);
        b.Property(x => x.ConnectorId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ProtectedTokensJson).IsRequired();
        b.HasIndex(x => new { x.UserId, x.ConnectorId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.ConnectorId }); // the disable-revokes sweep
    }
}

internal sealed class ConnectorBindingConfiguration : IEntityTypeConfiguration<ConnectorBinding>
{
    public void Configure(EntityTypeBuilder<ConnectorBinding> b)
    {
        b.ToTable("connector_bindings");
        b.HasKey(x => x.Id);
        b.Property(x => x.ConnectorId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResourceType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ExternalRef).HasMaxLength(1000).IsRequired();
        // One binding per resource (the Harvey pattern) — rebinding replaces, never accumulates.
        b.HasIndex(x => new { x.TenantId, x.ModuleId, x.ResourceType, x.ResourceId }).IsUnique();
    }
}

internal sealed class RagCollectionConfiguration : IEntityTypeConfiguration<RagCollection>
{
    public void Configure(EntityTypeBuilder<RagCollection> b)
    {
        b.ToTable("rag_collections");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResourceType).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(300).IsRequired();
        b.Property(x => x.EmbeddingModel).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ModuleId, x.ResourceType, x.ResourceId });
        b.HasIndex(x => new { x.TenantId, x.Name });
    }
}

internal sealed class RagChunkConfiguration : IEntityTypeConfiguration<RagChunk>
{
    public void Configure(EntityTypeBuilder<RagChunk> b)
    {
        b.ToTable("rag_chunks");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(300).IsRequired();
        b.Property(x => x.Text).IsRequired();
        b.Property(x => x.EmbeddingModel).HasMaxLength(100).IsRequired();
        b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        // The pgvector `embedding` and generated `tsv` columns are added by the migration's raw SQL
        // and are deliberately NOT mapped — see RagChunk. Composite indexes lead with TenantId so
        // the hybrid query's predicates stay indexed.
        b.HasIndex(x => new { x.TenantId, x.CollectionId });
        b.HasIndex(x => new { x.CollectionId, x.FileId });
    }
}

internal sealed class PendingApprovalConfiguration : IEntityTypeConfiguration<PendingApproval>
{
    public void Configure(EntityTypeBuilder<PendingApproval> b)
    {
        b.ToTable("pending_approvals");
        b.HasKey(x => x.Id);
        b.Property(x => x.ModuleId).HasMaxLength(64).IsRequired();
        b.Property(x => x.ToolName).HasMaxLength(128).IsRequired();
        b.Property(x => x.ArgumentsJson).HasColumnType("jsonb");
        b.Property(x => x.Result).HasMaxLength(2000);
        b.Property(x => x.Error).HasMaxLength(2000);
        b.Property(x => x.UserDisplay).HasMaxLength(200);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => x.ConversationId);
    }
}
