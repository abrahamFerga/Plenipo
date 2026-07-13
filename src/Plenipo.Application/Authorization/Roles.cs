namespace Plenipo.Application.Authorization;

/// <summary>
/// System-level roles (Layer 1 of the RBAC model). These are coarse and global; fine-grained access
/// is expressed as <see cref="Permissions">permissions</see> (Layer 2) and per-resource ACLs (Layer 3).
/// </summary>
public static class Roles
{
    /// <summary>Platform operator. Implicitly holds every permission across every tenant.</summary>
    public const string SystemAdmin = "system_admin";

    /// <summary>Administers a single tenant: its users, role assignments, and enabled modules.</summary>
    public const string TenantAdmin = "tenant_admin";

    /// <summary>Standard authenticated user.</summary>
    public const string User = "user";

    /// <summary>Read-only access; may not execute tools.</summary>
    public const string Guest = "guest";

    public static readonly IReadOnlyList<string> All = [SystemAdmin, TenantAdmin, User, Guest];
}
