using System.Text.Json;
using Plenipo.Application.Auditing;
using Plenipo.Core.Entities;
using Plenipo.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Plenipo.Infrastructure.Persistence.Interceptors;

/// <summary>
/// On every save: stamps <see cref="IAuditable"/> provenance columns and captures a create/update/delete
/// record for each changed entity, flushed to the audit store after the operation commits. This is the
/// "audit every data change" half of the platform's audit guarantee; the agent tool-call half lives in
/// the agent runner.
/// </summary>
public sealed class AuditInterceptor(ICurrentUser currentUser, IAuditLog auditLog) : SaveChangesInterceptor
{
    private readonly List<EntityChangeAuditEntry> _pending = [];

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            Capture(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (_pending.Count > 0)
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            await auditLog.RecordEntityChangesAsync(batch, cancellationToken);
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext context)
    {
        var actor = currentUser.DisplayName ?? currentUser.Subject ?? "system";
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is IAuditable auditable)
            {
                StampAuditColumns(entry, auditable, actor, now);
            }

            // Chunks are derived projections of already-audited files, written hundreds at a time
            // by an audited tool call + job — per-row entries would only bury the real audit trail.
            if (entry.Entity is Core.Platform.RagChunk)
            {
                continue;
            }

            var kind = entry.State switch
            {
                EntityState.Added => (EntityChangeKind?)EntityChangeKind.Created,
                EntityState.Modified => EntityChangeKind.Modified,
                EntityState.Deleted => EntityChangeKind.Deleted,
                _ => null,
            };

            if (kind is null)
            {
                continue;
            }

            _pending.Add(new EntityChangeAuditEntry
            {
                TenantId = currentUser.TenantId,
                UserId = currentUser.UserId,
                UserDisplay = actor,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = TryGetId(entry),
                Kind = kind.Value,
                ChangesJson = kind == EntityChangeKind.Modified ? SerializeChanges(entry) : null,
                OccurredAt = now,
            });
        }
    }

    private static void StampAuditColumns(EntityEntry entry, IAuditable auditable, string actor, DateTimeOffset now)
    {
        if (entry.State == EntityState.Added)
        {
            auditable.CreatedAt = now;
            auditable.CreatedBy = actor;
        }
        else if (entry.State == EntityState.Modified)
        {
            auditable.UpdatedAt = now;
            auditable.UpdatedBy = actor;
        }
    }

    private static string TryGetId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is not { Properties.Count: > 0 })
        {
            return string.Empty;
        }

        return entry.Property(key.Properties[0].Name).CurrentValue?.ToString() ?? string.Empty;
    }

    private static string SerializeChanges(EntityEntry entry)
    {
        var changes = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var prop in entry.Properties)
        {
            if (prop.IsModified && !Equals(prop.OriginalValue, prop.CurrentValue))
            {
                changes[prop.Metadata.Name] = [prop.OriginalValue, prop.CurrentValue];
            }
        }

        return JsonSerializer.Serialize(changes);
    }
}
