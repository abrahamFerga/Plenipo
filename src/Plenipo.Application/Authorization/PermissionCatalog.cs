namespace Plenipo.Application.Authorization;

/// <summary>Describes a single permission for the security dashboard: its string, category, and intent.</summary>
public sealed record PermissionInfo(string Permission, string Category, string Description);

/// <summary>
/// The catalog of built-in platform permissions, surfaced to the admin/security dashboard so an
/// operator can see — and reason about — every capability the RBAC system can grant. Module tool
/// permissions are discovered separately from each module's manifest and merged in at the endpoint.
/// This is the platform analogue of OpenClaw's explicit, inspectable tool-permission map.
/// </summary>
public static class PermissionCatalog
{
    public const string PlatformCategory = "Platform administration";
    public const string ChatCategory = "Chat & agents";
    public const string FilesCategory = "Files & documents";

    /// <summary>Every built-in (non-module) permission, with a human description for the dashboard.</summary>
    public static readonly IReadOnlyList<PermissionInfo> Platform =
    [
        new(Permissions.ManageTenants, PlatformCategory, "Create, edit, and deactivate tenants."),
        new(Permissions.ManageUsers, PlatformCategory, "Provision users and manage their profile and status."),
        new(Permissions.ManageRoles, PlatformCategory, "Assign roles and grant or revoke permissions."),
        new(Permissions.ManageModules, PlatformCategory, "Enable or disable domain modules for a tenant."),
        new(Permissions.ManageConnectors, PlatformCategory, "Enable, disable, and configure data-source connectors for a tenant."),
        new(Permissions.ManageAiSettings, PlatformCategory, "Configure the tenant's AI: the provider connection (model, vaulted API key), system prompt, token budgets, agent profiles, and instruction-snapshot lookup."),
        new(Permissions.ManageNotifications, PlatformCategory, "Configure notification delivery (webhook URL and signing secret)."),
        new(Permissions.ViewAuditLog, PlatformCategory, "Read the audit log and token-usage telemetry."),
        new(Permissions.UseChat, ChatCategory, "Start conversations and message the agent."),
        new(Permissions.ViewConversations, ChatCategory, "Read existing conversation history."),
        new(Permissions.ManageApprovals, ChatCategory, "Approve or reject side-effecting tool calls (human-in-the-loop)."),
        new(Permissions.UploadFiles, FilesCategory, "Upload files (chat attachments) to the tenant file store."),
        new(Permissions.ReadFiles, FilesCategory, "Download files from the tenant file store."),
        new("tools.documents.read_document", FilesCategory, "Agent tool: extract the text of a stored PDF or text file."),
        new("tools.documents.generate_pdf", FilesCategory, "Agent tool: generate a PDF document and store it."),
        new("tools.documents.list_documents", FilesCategory, "Agent tool: list the caller's stored files."),
        new("tools.documents.ocr_document", FilesCategory, "Agent tool: OCR a scanned document (requires a configured OCR engine)."),
        new("tools.knowledge.search_knowledge", FilesCategory, "Agent tool: search indexed knowledge collections for cited passages (requires Rag:Enabled)."),
        new("tools.skills.load_skill", ChatCategory, "Agent tool: load an installed skill's instructions on demand (requires Skills:Enabled)."),
        new("tools.skills.read_skill_resource", ChatCategory, "Agent tool: read a resource file bundled with an installed skill."),
        new("tools.skills.run_skill_script", ChatCategory, "Agent tool: run a script bundled with an installed skill (side-effecting; approval-gated)."),
        new("tools.handoff.ask_module", ChatCategory, "Agent tool: ask another enabled module's assistant a read-only question (ask_finance, ask_legal, …) and relay the answer."),
        new("tools.mcp.*", ChatCategory, "Agent tools from configured external MCP servers (each discovered tool is tools.mcp.{server}_{tool}; approval-gated by default; granted to no role until an admin opts in)."),
    ];
}
