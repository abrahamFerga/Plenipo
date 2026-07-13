using System.Text.Json;
using Plenipo.Core.Entities;

namespace Plenipo.Modules.Legal.Persistence;

public enum MatterStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// A legal matter — the engagement-scoped workspace every legal-AI product organizes around
/// (Harvey's Vault, Legora's workspaces). Documents, drafts, and agent work product attach to a
/// matter; tenant isolation applies via the module's query filters.
/// </summary>
public sealed class Matter : TenantEntityBase
{
    public required string Name { get; set; }

    /// <summary>The client this matter is for (display-level; the PM system stays the system of record).</summary>
    public string? ClientName { get; set; }

    public MatterStatus Status { get; set; } = MatterStatus.Open;

    /// <summary>
    /// The ethical wall (legal v1 item 10): null means open to the whole tenant; a JSON array of
    /// user ids means ONLY those users can see or use the matter — everywhere (tools, tabs, and
    /// the matter's RAG collection). Walls restrict by identity, not permission, so even a
    /// wildcard-permission user outside the wall is excluded.
    /// </summary>
    public string? RestrictedUserIdsJson { get; set; }

    public ICollection<MatterDocument> Documents { get; set; } = [];

    /// <summary>Whether <paramref name="userId"/> is inside this matter's wall (or there is none).</summary>
    public bool IsAccessibleTo(Guid? userId) => WallAllows(RestrictedUserIdsJson, userId);

    /// <summary>The wall check itself, usable on projections that carry only the JSON column.</summary>
    public static bool WallAllows(string? restrictedUserIdsJson, Guid? userId)
    {
        if (restrictedUserIdsJson is null)
        {
            return true;
        }

        if (userId is null)
        {
            return false; // fail closed: no identity, no walled access
        }

        try
        {
            var allowed = JsonSerializer.Deserialize<Guid[]>(restrictedUserIdsJson) ?? [];
            return allowed.Contains(userId.Value);
        }
        catch (JsonException)
        {
            return false; // fail closed on a corrupt wall rather than opening the matter
        }
    }
}

/// <summary>
/// A platform file (<c>StoredFile</c>) attached to a matter. The platform file store keeps the bytes
/// and metadata; this row is the matter-scoped association plus a display-name snapshot.
/// </summary>
public sealed class MatterDocument : TenantEntityBase
{
    public Guid MatterId { get; set; }

    /// <summary>The platform <c>StoredFile</c> id — what the document tools (read_document) consume.</summary>
    public Guid FileId { get; set; }

    public required string FileName { get; set; }

    /// <summary>Optional note recorded at attach time (e.g. "signed original", "client draft").</summary>
    public string? Note { get; set; }
}
