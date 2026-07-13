namespace Plenipo.Core.Entities;

/// <summary>Base for platform-owned (non-tenant) entities with a GUID identity and audit columns.</summary>
public abstract class EntityBase : IAuditable
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
