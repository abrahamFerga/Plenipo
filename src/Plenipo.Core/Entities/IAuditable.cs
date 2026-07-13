namespace Plenipo.Core.Entities;

/// <summary>
/// Entities that carry creation / modification provenance. Populated automatically by the
/// persistence layer's audit interceptor — application code never sets these by hand.
/// </summary>
public interface IAuditable
{
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
