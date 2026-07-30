// ─────────────────────────────────────────────────────────────────────────────
// @plenipo/ui — public library entry point
//
// Importing from this package gives you the base platform's *domain* (end-user)
// shell components, hooks, and API utilities. Domain-specific components
// (TransactionsTab, etc.) live in their own packages (e.g. @plenipo/finance-ui).
//
// The *admin* console (security / RBAC / users / token usage / audit) is a
// separate surface — the @plenipo/admin-ui app, served at /admin. It consumes the
// API client and admin types re-exported below, but its UI does not ship here.
// ─────────────────────────────────────────────────────────────────────────────

// Shell. `PlenipoApp` is the batteries-included entry (query client + router + shell); `AppShell` is
// the shell alone for hosts that own their router/query client. (App.tsx / main.tsx are the local
// dev harness — not part of the public API. See the README for the composition snippets.)
export { PlenipoApp } from "./components/PlenipoApp";
export type { PlenipoAppProps } from "./components/PlenipoApp";
export { AppShell } from "./routes/AppShell";
export type { AppShellProps } from "./routes/AppShell";

// Module UI registry — register host React components per module tab (generic fallback otherwise).
export { defineModule, createModuleUiRegistry, resolveTabComponent } from "./lib/moduleUi";
export type { PlenipoModuleUi, ModuleTabProps, ModuleUiRegistry } from "./lib/moduleUi";

// Branding — a host's product name + logo for the top bar (accent color is themed via CSS variables).
export { BrandingContext, useBranding } from "./lib/branding";
export type { PlenipoBranding } from "./lib/branding";

// Components
export { ModuleTabView } from "./components/ModuleTabView";
export { TabErrorBoundary } from "./components/TabErrorBoundary";
export { AppErrorBoundary } from "./components/AppErrorBoundary";
export { PermissionGate } from "./components/PermissionGate";
export { ConnectedAccounts } from "./components/ConnectedAccounts";
export { AiDecisionLog } from "./components/AiDecisionLog";
export { ChatPanel } from "./components/ChatPanel";
export { ChatView } from "./components/ChatView";
export { ConversationList } from "./components/ConversationList";
export { Markdown } from "./components/Markdown";
export { PendingApprovals } from "./components/PendingApprovals";
export { DemoModeBanner } from "./components/DemoModeBanner";
export { ApiUnreachable } from "./components/ApiUnreachable";
export { AccessDenied } from "./components/AccessDenied";
export { TopBar } from "./components/TopBar";
export { Sidebar } from "./components/Sidebar";
export { BottomNav } from "./components/BottomNav";
export { GenericTab } from "./components/GenericTab";
export { ModuleSwitcher } from "./components/ModuleSwitcher";
export { ConfirmDialog } from "./components/ConfirmDialog";
export { ThemeToggle } from "./components/ThemeToggle";
export { NotificationBell } from "./components/NotificationBell";

// Dashboard primitives — composition pieces for a product's own overview tab (registered via the
// module UI registry): the shell ships the pieces, the product owns the layout.
export { StatTile } from "./components/StatTile";
export type { StatTileProps } from "./components/StatTile";
export { ProgressBar } from "./components/ProgressBar";
export type { ProgressBarProps } from "./components/ProgressBar";

// Theme (dark mode): call initTheme() at startup; ThemeToggle (or useTheme) drives the preference.
export { initTheme, useTheme, resolveTheme, getThemePreference, setThemePreference } from "./lib/theme";
export type { ThemePreference } from "./lib/theme";

// Hooks
export { useModules } from "./hooks/useModules";
export { useMe } from "./hooks/useMe";
export { useInfo } from "./hooks/useInfo";
export { usePermission } from "./hooks/usePermission";
export { useMediaQuery, NARROW_QUERY } from "./hooks/useMediaQuery";

// Types
export type {
  Me,
  Module,
  ModuleTab,
  TabColumn,
  TabEditor,
  TabEditorField,
  TabEditorOption,
  TabRowAction,
  TabDetailDocument,
  TabDetailSection,
  TabSectionTone,
  Conversation,
  ConversationMessage,
  PlatformInfo,
  PendingApproval,
  AiDecision,
  SecurityCatalog,
  PermissionInfo,
  ModuleSecurity,
  RoleInfo,
  ModuleAdmin,
  AdminExtension,
  ConnectorAdmin,
  ConnectorCatalog,
  AvailableConnector,
  ConnectorSetting,
  AdminTenant,
  AiSettings,
  AgentProfile,
  ModuleAgent,
  ModuleSkill,
  NotificationInfo,
  NotificationPreference,
  NotificationSettings,
  UserConnector,
  OpsSnapshot,
  AdminUser,
  UserInviteAdmin,
  ToolCall,
  AuthEvent,
  UsageReport,
  UsageByModule,
  UsageByDay,
} from "./lib/api";
export type { AgentStreamEvent } from "./lib/signalr";
export type { AguiEvent } from "./lib/agui";

// Authentication — the seam that lets a product sign someone in without forking this package.
// `configureClient` is re-exported from the BUNDLED @plenipo/client instance: importing it from
// "@plenipo/client" in a consuming app configures a different module instance than the one these
// components read, which is why overriding it from outside never reached the shipped shell.
export {
  configurePlenipoWeb,
  plenipoWebAuth,
  plenipoWebMode,
  signOutPlenipoWeb,
  resetPlenipoWeb,
  devAuthOnly,
  memoryStorage,
  sessionStorageAdapter,
} from "./lib/webAuth";
export type { AuthAdapter, SecureStorageAdapter, PlenipoWebConfig } from "./lib/webAuth";
export { createOidcAuth, isSignInCallback } from "./lib/oidc";
export type { OidcOptions, OidcAuthAdapter } from "./lib/oidc";
export { initPlenipoWebAuth } from "./lib/initWebAuth";
export type { InitWebAuthOptions, WebAuthState } from "./lib/initWebAuth";
export { configureClient, clientConfig, devAuthHeaders, fetchAuthConfig } from "./lib/api";
export type { AuthConfig } from "./lib/api";

// API / lib utilities
export { apiGet, apiSend, apiPost, api, ApiError, uploadFile } from "./lib/api";
export type { StoredFileInfo } from "./lib/api";
export { hasPermission } from "./lib/permissions";
export { withAttachmentRefs, parseAttachmentRefs } from "./lib/attachments";
export type { AttachmentRef } from "./lib/attachments";
export { createAgentConnection } from "./lib/signalr";
export { runAgui } from "./lib/agui";
// Active-module context + hook — let a host tab component read the module list / active module and switch it.
export { ActiveModuleContext, useActiveModule } from "./lib/activeModule";
export type { ActiveModuleContextValue } from "./lib/activeModule";
