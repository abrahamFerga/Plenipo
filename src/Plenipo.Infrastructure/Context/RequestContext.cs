using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Infrastructure.Context;

/// <summary>
/// The single scoped object that carries "who and which tenant" for the current operation. It backs
/// both <see cref="ICurrentUser"/> and <see cref="ITenantContext"/> so the rest of the system depends
/// on small role-specific interfaces while the request pipeline populates one place. Middleware fills
/// it in order: tenant first (so query filters are correct), then the provisioned user, then the
/// resolved permission set.
/// </summary>
public sealed class RequestContext : ICurrentUser, ITenantContext
{
    private HashSet<string> _permissions = new(StringComparer.Ordinal);

    public Guid? UserId { get; private set; }
    public string? Subject { get; private set; }
    public string? DisplayName { get; private set; }
    public Guid? TenantId { get; private set; }

    public bool IsAuthenticated => Subject is not null;
    public bool HasTenant => TenantId is not null;

    public IReadOnlySet<string> Permissions => _permissions;

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("No tenant has been resolved for the current operation.");

    public bool HasPermission(string permission) => PermissionMatcher.IsGranted(_permissions, permission);

    // --- population API (used only by the request pipeline / background scopes) ---

    public void SetTenant(Guid tenantId) => TenantId = tenantId;

    public void SetUser(Guid userId, string subject, string? displayName)
    {
        UserId = userId;
        Subject = subject;
        DisplayName = displayName;
    }

    public void SetIdentity(string subject, string? displayName)
    {
        Subject = subject;
        DisplayName = displayName;
    }

    public void SetPermissions(IEnumerable<string> permissions) =>
        _permissions = new HashSet<string>(permissions, StringComparer.Ordinal);
}
