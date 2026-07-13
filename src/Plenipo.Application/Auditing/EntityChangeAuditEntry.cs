namespace Plenipo.Application.Auditing;

public enum EntityChangeKind
{
    Created = 0,
    Modified = 1,
    Deleted = 2,
}

/// <summary>
/// Append-only record of a create/update/delete to an auditable entity, captured automatically by the
/// EF Core save interceptor.
/// </summary>
public sealed class EntityChangeAuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid? TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? UserDisplay { get; init; }

    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public required EntityChangeKind Kind { get; init; }

    /// <summary>JSON map of changed property -> { old, new }. Null for creates/deletes.</summary>
    public string? ChangesJson { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
