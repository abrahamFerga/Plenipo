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
/// <summary>Why the current request has no tenant. Diagnostic only — it never affects a decision.</summary>
public enum TenantResolution
{
    /// <summary>A tenant was resolved, or the operation never needed one (a background scope).</summary>
    Resolved = 0,

    /// <summary>The principal named a tenant that does not exist in this deployment.</summary>
    ClaimDidNotMatch = 1,

    /// <summary>The principal named no tenant and the deployment does not have exactly one to fall back to.</summary>
    NoClaimAndAmbiguous = 2,
}

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

    /// <summary>
    /// Whether tenant resolution succeeded, and if not why. Deliberately NOT on <see cref="ICurrentUser"/>:
    /// nothing may authorize on it, and adding it there would invite exactly that. It exists so an
    /// authenticated request whose tenant did not resolve can SAY so instead of silently carrying an empty
    /// permission set and failing every call with a bare 403.
    /// </summary>
    public TenantResolution Resolution { get; private set; } = TenantResolution.Resolved;

    /// <summary>The tenant the principal asked for, when it did not resolve. Echoed back, never invented.</summary>
    public string? RequestedTenant { get; private set; }

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("No tenant has been resolved for the current operation.");

    public bool HasPermission(string permission) => PermissionMatcher.IsGranted(_permissions, permission);

    // --- population API (used only by the request pipeline / background scopes) ---

    public void SetTenant(Guid tenantId) => TenantId = tenantId;

    /// <summary>Records that tenant resolution failed, and why, so the failure can name its own cause.</summary>
    public void SetTenantUnresolved(TenantResolution reason, string? requestedTenant)
    {
        Resolution = reason;
        RequestedTenant = requestedTenant;
    }

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
