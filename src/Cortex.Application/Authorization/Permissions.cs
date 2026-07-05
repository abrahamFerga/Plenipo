namespace Cortex.Application.Authorization;

/// <summary>
/// Permission strings (Layer 2 of the RBAC model). A permission is a dotted, hierarchical capability
/// such as <c>tools.finance.categorize</c> or <c>platform.users.manage</c>. Endpoints and tools are
/// gated on these; the agent runner filters tools by the tool's permission before the model call.
/// </summary>
public static class Permissions
{
    // Platform administration
    public const string ManageTenants = "platform.tenants.manage";
    public const string ManageUsers = "platform.users.manage";
    public const string ManageRoles = "platform.roles.manage";
    public const string ManageModules = "platform.modules.manage";
    public const string ManageConnectors = "platform.connectors.manage";
    public const string ManageAiSettings = "platform.ai.manage";
    public const string ManageNotifications = "platform.notifications.manage";
    public const string ViewAuditLog = "platform.audit.view";

    // Chat / agent surface
    public const string UseChat = "chat.use";
    public const string ViewConversations = "chat.conversations.view";

    /// <summary>Approve or reject side-effecting tool calls the agent was blocked from auto-running (HITL).</summary>
    public const string ManageApprovals = "chat.approvals.manage";

    // Files & documents (chat attachments, agent document tools)
    public const string UploadFiles = "files.upload";
    public const string ReadFiles = "files.read";

    /// <summary>The pseudo-module id platform document tools are namespaced under (tools.documents.*).</summary>
    public const string DocumentsToolModule = "documents";

    /// <summary>The pseudo-module id the RAG search tool is namespaced under (tools.knowledge.*).</summary>
    public const string KnowledgeToolModule = "knowledge";

    /// <summary>The pseudo-module id the agent-skill tools are namespaced under (tools.skills.*).</summary>
    public const string SkillsToolModule = "skills";

    /// <summary>The pseudo-module id the cross-module handoff tools are namespaced under (tools.handoff.*).</summary>
    public const string HandoffToolModule = "handoff";

    /// <summary>
    /// Permissions reserved for the platform operator (system_admin): they act ACROSS tenants, so a
    /// tenant-scoped admin must never hold them. The RBAC editor refuses to grant these — or any wildcard that
    /// covers them (<c>platform.*</c>, <c>*</c>) — to a role or user unless the caller already holds them, which
    /// keeps a tenant admin from escalating back into cross-tenant control. Currently: cross-tenant management.
    /// </summary>
    public static readonly IReadOnlyList<string> OperatorOnly = [ManageTenants];

    /// <summary>Prefix every module tool permission shares: <c>tools.&lt;module&gt;.&lt;tool&gt;</c>.</summary>
    public const string ToolPrefix = "tools.";

    /// <summary>Builds the conventional permission for a module tool.</summary>
    public static string ForTool(string moduleId, string toolName) => $"{ToolPrefix}{moduleId}.{toolName}";

    /// <summary>Builds the conventional wildcard covering all of a module's tools.</summary>
    public static string AllToolsFor(string moduleId) => $"{ToolPrefix}{moduleId}.*";

    /// <summary>
    /// Builds the conventional permission for a connector tool: <c>tools.connectors.&lt;id&gt;.&lt;tool&gt;</c>.
    /// Staying under the tools.* umbrella keeps wildcards, the RBAC editor, and the security catalog
    /// working unchanged; the extra segment keeps connector grants distinct from module grants.
    /// </summary>
    public static string ForConnectorTool(string connectorId, string toolName) =>
        $"{ToolPrefix}connectors.{connectorId}.{toolName}";
}
