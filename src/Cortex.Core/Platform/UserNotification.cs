using Cortex.Core.Entities;
using Cortex.Core.Multitenancy;

namespace Cortex.Core.Platform;

/// <summary>
/// The in-app notification inbox row — the baseline delivery every notification gets regardless
/// of which extra channels a deployment configures. Unread until the user marks it.
/// </summary>
public sealed class UserNotification : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Producer namespace for filtering/badging, e.g. "jobs", "calendar", "connectors".</summary>
    public required string Category { get; set; }

    public required string Title { get; set; }

    public required string Body { get; set; }

    /// <summary>Optional app-relative link to the subject (e.g. /api/jobs/{id}).</summary>
    public string? Link { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}
