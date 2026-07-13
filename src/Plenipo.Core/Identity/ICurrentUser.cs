namespace Plenipo.Core.Identity;

/// <summary>
/// The authenticated user behind the current operation. Backed by the request principal in the API
/// and resolvable from background workers via the operation's captured context.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Stable platform user id (our row), or <c>null</c> when unauthenticated.</summary>
    public Guid? UserId { get; }

    /// <summary>The external identity provider subject (OIDC <c>sub</c>) for this user.</summary>
    public string? Subject { get; }

    /// <summary>Display name / email, best-effort, for audit attribution.</summary>
    public string? DisplayName { get; }

    /// <summary>Tenant the user is acting within.</summary>
    public Guid? TenantId { get; }

    public bool IsAuthenticated { get; }

    /// <summary>Permission strings granted for this request (claims + tenant grants merged).</summary>
    public IReadOnlySet<string> Permissions { get; }

    public bool HasPermission(string permission);
}
