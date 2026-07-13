namespace Plenipo.Application.Authorization;

/// <summary>
/// Maps system roles (Layer 1) to the baseline permissions (Layer 2) they imply. Tool permissions are
/// granted on top of this, either explicitly per user or by a tenant admin — roles alone never unlock
/// an arbitrary module's tools.
/// </summary>
public static class RolePermissions
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        // system_admin holds the global wildcard — everything, every tenant.
        [Roles.SystemAdmin] = ["*"],

        // tenant_admin administers their OWN tenant — its users, roles, modules, AI settings, and audit — and
        // uses chat. Deliberately NOT platform.* : that wildcard also covers platform.tenants.manage, which is
        // cross-tenant (create / deactivate ANY tenant) and reserved for the operator (system_admin). Listing
        // each capability explicitly also makes any future platform permission deny-by-default for tenant admins.
        [Roles.TenantAdmin] =
        [
            Permissions.ManageUsers,
            Permissions.ManageRoles,
            Permissions.ManageModules,
            Permissions.ManageConnectors,
            Permissions.ManageAiSettings,
            Permissions.ManageNotifications,
            Permissions.ViewAuditLog,
            "chat.*",
            "files.*",
            "tools.documents.*",
            "tools.knowledge.*",
            // Skills are deploy-time content and scripts are approval-gated; admins get the loop.
            "tools.skills.*",
            // Handoffs are read-only nested turns; the nested RBAC filter still applies inside.
            "tools.handoff.*",
        ],

        // user can chat, see their conversations, attach files, and use the platform's read-only
        // document tools (extract text, list own files, generate a PDF) plus knowledge search —
        // it only ever surfaces collections whose gates admit the caller. Module tools stay opt-in.
        [Roles.User] =
        [
            Permissions.UseChat,
            Permissions.ViewConversations,
            Permissions.UploadFiles,
            Permissions.ReadFiles,
            "tools.documents.read_document",
            "tools.documents.generate_pdf",
            "tools.documents.list_documents",
            "tools.documents.ocr_document",
            "tools.knowledge.search_knowledge",
        ],

        // guest is read-only.
        [Roles.Guest] = [Permissions.ViewConversations],
    };

    public static IEnumerable<string> ForRole(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : [];

    public static IEnumerable<string> ForRoles(IEnumerable<string> roles) =>
        roles.SelectMany(ForRole).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// The built-in defaults, used to seed a tenant's editable role → permission rows and as the fallback
    /// for any tenant that has none. Exposed read-only; the live, per-tenant mapping is stored in the DB.
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Defaults => Map;
}
