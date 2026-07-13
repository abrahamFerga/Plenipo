namespace Plenipo.Core.Entities;

/// <summary>Entities that are never hard-deleted; the audit trail must survive removal.</summary>
public interface ISoftDeletable
{
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
