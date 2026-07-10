import { API_BASE, devAuthHeaders } from "./devAuth";

/** Shape of the current user from GET /api/platform/me. */
export interface Me {
  userId: string | null;
  displayName: string | null;
  tenantId: string | null;
  permissions: string[];
}

/** A column in a tab's server-driven data view. */
export interface TabColumn {
  field: string;
  header: string;
}

/** A field in a tab's generic editor form. */
export interface TabEditorField {
  field: string;
  label: string;
  multiline?: boolean;
  required?: boolean;
  /** Render a number input and post a JSON number (for endpoints binding decimal/int). */
  numeric?: boolean;
}

/**
 * Mutation affordances for a server-driven tab (add/edit/delete on the generic table). Present in
 * the payload ONLY when the caller holds the module-declared permission — the endpoints stay
 * authorization-gated server-side regardless.
 */
export interface TabEditor {
  /** POST target for add and edit; the body is a JSON object of `fields` values. */
  upsertEndpoint: string;
  /** DELETE target with one `{field}` placeholder substituted from the row. Absent = no delete. */
  deleteEndpoint?: string;
  /** Row field identifying a record for editing (read-only in the edit form). Absent = add/delete only. */
  keyField?: string;
  fields: TabEditorField[];
}

/** Render the tab's dataEndpoint rows as a time-series line chart instead of a table. */
export interface TabChart {
  /** Row field holding the x value — an ISO date string. */
  xField: string;
  /** Row field holding the numeric y value. */
  yField: string;
  /** Optional row field splitting rows into one line per value (e.g. a currency code). */
  seriesField?: string | null;
  /** Axis label for the measure. */
  yLabel?: string | null;
}

/** A tab-level command button: POST (empty body) to `endpoint`, show the response, refresh. */
export interface TabAction {
  id: string;
  label: string;
  endpoint: string;
  /** Confirmation prompt shown before the POST, for consequential actions. */
  confirm?: string | null;
}

/** A tab inside a module. */
export interface ModuleTab {
  id: string;
  label: string;
  route: string;
  icon?: string;
  /** When set, the shell renders this GET endpoint's JSON array as a table (using `columns`). */
  dataEndpoint?: string;
  columns?: TabColumn[];
  /** Friendly empty-state copy shown when the tab has no `dataEndpoint` and no supplied content. */
  placeholder?: string;
  /** Add/edit/delete affordances for the table; present only when the caller may use them. */
  editor?: TabEditor | null;
  /** Drill-down endpoint with one `{field}` placeholder resolved from the row; returns a TabDetailDocument. */
  detailEndpoint?: string | null;
  /** When set, the shell renders the dataEndpoint rows as a line chart instead of a table. */
  chart?: TabChart | null;
  /** Command buttons; present only for callers allowed to invoke them. */
  actions?: TabAction[];
}

/** A section of a drill-down detail document: prose, or a sub-table. */
export interface TabDetailSection {
  heading: string;
  text?: string;
  columns?: TabColumn[];
  rows?: Record<string, unknown>[];
}

/** The generic drill-down document a tab's `detailEndpoint` returns. */
export interface TabDetailDocument {
  title: string;
  subtitle?: string;
  sections: TabDetailSection[];
}

/** An entry in the chat's agent picker: a tenant profile or a module-shipped agent. */
export interface ModuleAgent {
  name: string;
  description?: string | null;
  /** The agent that applies when the user picks none. */
  isDefault: boolean;
  /** Model this agent pins, if any (the picker shows it as a hint). */
  model?: string | null;
}

/** A skill invocable from the composer with "/name", from the module payload. */
export interface ModuleSkill {
  name: string;
  description: string;
}

/** A module returned from GET /api/platform/modules. */
/** One step of a module's first-run setup wizard. */
export interface OnboardingStep {
  id: string;
  title: string;
  blurb: string;
  /** info | form | upload. */
  kind: string;
  /** form: POST target. upload: the follow-up POST receiving the stored file id. */
  endpoint?: string | null;
  fields?: TabEditorField[];
  /** Constant values merged into every POST body (e.g. direction=income). */
  preset?: Record<string, string> | null;
  /** upload: the body field carrying the stored file id. */
  fileIdField?: string | null;
  /** upload: accepted file types, e.g. ".csv,.ofx,.qfx,.pdf". */
  accept?: string | null;
  optional?: boolean;
}

/** A module's declared setup wizard; present only for callers who may run it. */
export interface Onboarding {
  /** GET endpoint returning an array; empty = unconfigured, the shell offers the wizard. */
  probeEndpoint: string;
  title: string;
  steps: OnboardingStep[];
}

export interface Module {
  id: string;
  displayName: string;
  description?: string;
  icon?: string;
  tabs: ModuleTab[];
  /** Example prompts the chat surfaces as one-click starters. */
  suggestedPrompts?: string[];
  /** Selectable agents for this module's chat (tenant profiles + module-shipped). */
  agents?: ModuleAgent[];
  /** Skills invocable in this module's chat (global library + module-shipped). */
  skills?: ModuleSkill[];
  /** First-run setup wizard, when the module declares one and the caller may run it. */
  onboarding?: Onboarding | null;
}

/** A chat conversation from GET /api/chat/conversations. */
export interface Conversation {
  id: string;
  moduleId: string;
  title: string;
  updatedAt: string;
}

/** A persisted message in a conversation, from GET /api/chat/conversations/{id}/messages. */
export interface ConversationMessage {
  id: string;
  role: "User" | "Assistant";
  content: string;
}

/** Deployment facts the shell uses to set expectations, from GET /api/platform/info. */
export interface PlatformInfo {
  chatEnabled: boolean;
  demoMode: boolean;
  /** The server's attachment limit (bytes) — the composer preflights against it. */
  maxUploadBytes: number;
  /** Models the chat's model picker offers (the runner enforces the same list). */
  availableModels: string[];
}

/** A side-effecting tool call awaiting human approval, from GET /api/chat/approvals. */
export interface PendingApproval {
  id: string;
  conversationId: string;
  moduleId: string;
  toolName: string;
  argumentsJson?: string;
  userDisplay?: string;
  createdAt: string;
}

// ── Admin / security dashboard shapes ────────────────────────────────────────

/** A single permission with metadata, from GET /api/admin/security/catalog. */
export interface PermissionInfo {
  permission: string;
  category: string;
  description: string;
  requiresApproval: boolean;
  audited: boolean;
}

/** A module's tools and the permission each requires (the security map). */
export interface ModuleSecurity {
  id: string;
  displayName: string;
  tools: PermissionInfo[];
}

/** The complete, inspectable permission map. */
export interface SecurityCatalog {
  platform: PermissionInfo[];
  modules: ModuleSecurity[];
}

/** A role and the baseline permissions it grants in the current tenant. */
export interface RoleInfo {
  role: string;
  permissions: string[];
  /** False for system_admin (fixed at the global wildcard); true for roles a tenant admin may edit. */
  editable: boolean;
  /** True for the four built-in system roles; false for custom (tenant-defined) roles. */
  builtIn: boolean;
}

/** An installed module and whether it's enabled for the current tenant, from GET /api/admin/modules. */
export interface ModuleAdmin {
  id: string;
  displayName: string;
  description?: string;
  enabled: boolean;
}

/** One admin-configurable connector setting (schema-driven; secrets are write-only). */
export interface ConnectorSetting {
  key: string;
  label: string;
  description?: string;
  required: boolean;
  isSecret: boolean;
  /** Whether a value is stored — for secrets, this is all the server will ever reveal. */
  hasValue: boolean;
}

/** An installed data-source connector + the current tenant's state, from GET /api/admin/connectors. */
export interface ConnectorAdmin {
  id: string;
  displayName: string;
  description: string;
  authMode: string;
  supportsSync: boolean;
  icon?: string;
  enabled: boolean;
  settings: ConnectorSetting[];
  tools: { name: string; description?: string; permission: string; requiresApproval: boolean }[];
}

/** A tenant in the deployment, from GET /api/admin/tenants (operator-only, cross-tenant). */
export interface AdminTenant {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  createdAt: string;
}

/** The tenant's AI overrides plus the deployment defaults, from GET /api/admin/ai-settings. */
export interface AiSettings {
  systemPromptOverride?: string;
  maxConversationTokensOverride?: number;
  maxMonthlyTokensOverride?: number;
  /** Tenant's own provider connection (null = deployment default). */
  providerOverride?: string | null;
  modelOverride?: string | null;
  endpointOverride?: string | null;
  /** Whether a tenant API key is on file. The key itself is write-only and never returned. */
  hasApiKey: boolean;
  defaultSystemPrompt: string;
  defaultMaxConversationTokens: number;
  defaultMaxMonthlyTokens: number;
  defaultProvider: string;
  defaultModel: string;
}

/** A named, per-module chatbot configuration, from GET /api/admin/agent-profiles. */
export interface AgentProfile {
  id: string;
  moduleId: string;
  name: string;
  instructions: string;
  /** How the instructions combine with the module's built-in ones. */
  mode: "Append" | "Replace";
  /** The default profile is the one the agent runner applies each turn. */
  isDefault: boolean;
  /**
   * Tool-name patterns this agent may use (exact names or a trailing `*` wildcard, e.g. `ask_*`).
   * Null/absent = every tool the caller's permissions allow. A selection only narrows RBAC.
   */
  toolNames?: string[] | null;
  /** Per-agent model within the tenant's provider. Null = the tenant/deployment default model. */
  model?: string | null;
}

/** A user with their roles and explicit permission grants. */
export interface AdminUser {
  id: string;
  /** External identity-provider subject (OIDC sub) — unique per tenant, unlike email. */
  subject: string;
  email: string;
  displayName?: string;
  isActive: boolean;
  lastSeenAt?: string;
  roles: string[];
  permissions: string[];
}

/** An in-app notification for the current user, from GET /api/notifications. */
export interface NotificationInfo {
  id: string;
  /** Producer namespace, e.g. "jobs", "budget", "calendar". */
  category: string;
  title: string;
  body: string;
  link?: string;
  createdAt: string;
  readAt?: string;
}

/** A delegated connector the current user can link, from GET /api/connectors. */
export interface UserConnector {
  id: string;
  displayName: string;
  description: string;
  icon?: string | null;
  /** Whether THIS user has linked their account. */
  connected: boolean;
}

/** Webhook delivery config, from GET /api/admin/notification-settings — the secret is write-only. */
export interface NotificationSettings {
  webhookUrl: string | null;
  hasWebhookSecret: boolean;
}

/** The one-call tenant health snapshot, from GET /api/admin/ops. */
export interface OpsSnapshot {
  jobs: {
    queued: number;
    running: number;
    failed24h: number;
    oldestQueuedAgeSeconds?: number;
  };
  connectors: { connectorId: string; bindingCount: number; lastSyncedAt?: string }[];
  rag: { collections: number; chunks: number; lastIngestAt?: string };
  notifications: { webhookConfigured: boolean };
  ai: { provider: string; model: string; monthTokens: number; maxMonthlyTokens: number };
}

/** A recorded agent tool invocation, from GET /api/admin/audit/tool-calls. */
export interface ToolCall {
  id: string;
  occurredAt: string;
  userDisplay?: string;
  moduleId: string;
  toolName: string;
  permission: string;
  success: boolean;
  error?: string;
  durationMs: number;
}

/** An identity / authorization audit event, from GET /api/admin/audit/auth-events. */
export interface AuthEvent {
  id: string;
  occurredAt: string;
  /** Serialized AuthAuditEventType, e.g. "SignIn", "PermissionGranted", "RolePermissionsChanged". */
  eventType: string;
  userDisplay?: string;
  subject?: string;
  detail?: string;
  ipAddress?: string;
}

/** Token usage rolled up by module. */
export interface UsageByModule {
  moduleId: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  turns: number;
}

/** Token usage for a single day. */
export interface UsageByDay {
  day: string;
  totalTokens: number;
}

/** The token usage report, from GET /api/admin/usage. */
export interface UsageReport {
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  turns: number;
  byModule: UsageByModule[];
  byDay: UsageByDay[];
}

/**
 * Thrown by {@link apiGet} / {@link apiSend} on a non-2xx response. Carries the HTTP `status` (so callers
 * can branch on it — a permission message on 403, a not-found state on 404 — without parsing `message`) and
 * the raw response `body`; the server's problem-details `detail`/`title` is also folded into `message`.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly statusText: string,
    message: string,
    /** Raw response body, when the server sent one (RFC 7807 problem-details JSON or plain text). */
    readonly body?: string,
  ) {
    super(message);
    this.name = "ApiError";
    // Keep `instanceof ApiError` reliable even if a consumer transpiles down to ES5.
    Object.setPrototypeOf(this, ApiError.prototype);
  }
}

/** Reads a response body as text, tolerating a missing/unreadable body (returns ""). */
async function readBody(res: Response): Promise<string> {
  try {
    return await res.text();
  } catch {
    return "";
  }
}

/**
 * A human-readable detail from an error body: an RFC 7807 problem-details object (`detail`/`title`/…), a bare
 * JSON string, or a short plain-text body. The JSON-string case matters because ASP.NET Core's
 * `Results.BadRequest("message")` serializes the string as JSON (`"message"`), so most of the platform's
 * validation errors arrive that way — without this they'd be dropped and the caller would see only "400".
 */
function problemDetail(body: string): string | undefined {
  const trimmed = body.trim();
  if (!trimmed) {
    return undefined;
  }
  try {
    const json = JSON.parse(trimmed) as unknown;
    // A bare JSON string body is itself the detail (e.g. Results.BadRequest("A custom role must …")).
    if (typeof json === "string") {
      return json.trim() || undefined;
    }
    if (json !== null && typeof json === "object") {
      const record = json as Record<string, unknown>;
      const candidate = record.detail ?? record.title ?? record.message ?? record.error;
      return typeof candidate === "string" && candidate.trim() ? candidate.trim() : undefined;
    }
    return undefined;
  } catch {
    // Not JSON — a short plain-text body is itself a usable detail; ignore large/HTML error pages.
    return trimmed.length <= 300 ? trimmed : undefined;
  }
}

/** Builds an {@link ApiError} from a failed response, folding any server detail into the message. */
async function toApiError(method: string, path: string, res: Response): Promise<ApiError> {
  const body = await readBody(res);
  const detail = problemDetail(body);
  const base = `${method} ${path} failed: ${res.status} ${res.statusText}`;
  return new ApiError(
    res.status,
    res.statusText,
    detail ? `${base} — ${detail}` : base,
    body || undefined,
  );
}

/**
 * Thin fetch wrapper that prefixes the API base URL and attaches dev-auth
 * headers. Throws an {@link ApiError} on non-2xx responses.
 */
export async function apiGet<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    method: "GET",
    headers: {
      Accept: "application/json",
      ...devAuthHeaders,
    },
  });

  if (!res.ok) {
    throw await toApiError("GET", path, res);
  }

  return (await res.json()) as T;
}

/**
 * POST for a tab-level action button (empty body). Returns the endpoint's `message` when the
 * response carries one — actions answer with what happened ("Posted 3 transaction(s)…"), and
 * the shell surfaces that instead of a mute refresh.
 */
export async function apiAction(path: string): Promise<string | undefined> {
  const res = await fetch(`${API_BASE}${path}`, {
    method: "POST",
    headers: { Accept: "application/json", ...devAuthHeaders },
  });
  if (!res.ok) {
    throw await toApiError("POST", path, res);
  }

  try {
    const json = (await res.json()) as { message?: unknown };
    return typeof json?.message === "string" && json.message.trim() ? json.message.trim() : undefined;
  } catch {
    return undefined; // 204 or non-JSON success — nothing to show, the refresh speaks.
  }
}

/** Fetch wrapper for mutations (POST / PUT / DELETE). Returns nothing; throws on non-2xx. */
export async function apiSend(
  path: string,
  method: "POST" | "PUT" | "DELETE",
  body?: unknown,
): Promise<void> {
  const res = await fetch(`${API_BASE}${path}`, {
    method,
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
      ...devAuthHeaders,
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!res.ok) {
    throw await toApiError(method, path, res);
  }
}

/** A file stored in the platform file store (chat attachments, agent-generated documents). */
export interface StoredFileInfo {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  createdAt?: string;
}

/**
 * Uploads one file (multipart) to the platform file store and returns its metadata. The returned id
 * is what chat messages reference and the agent's document tools consume.
 */
export async function uploadFile(file: File): Promise<StoredFileInfo> {
  const form = new FormData();
  form.append("file", file, file.name);

  const res = await fetch(`${API_BASE}/api/files/`, {
    method: "POST",
    headers: { Accept: "application/json", ...devAuthHeaders },
    body: form,
  });

  if (!res.ok) {
    throw await toApiError("POST", "/api/files/", res);
  }

  return (await res.json()) as StoredFileInfo;
}

export const api = {
  me: () => apiGet<Me>("/api/platform/me"),
  modules: () => apiGet<Module[]>("/api/platform/modules"),
  info: () => apiGet<PlatformInfo>("/api/platform/info"),
  conversations: (moduleId?: string) =>
    apiGet<Conversation[]>(
      `/api/chat/conversations${moduleId ? `?moduleId=${encodeURIComponent(moduleId)}` : ""}`,
    ),
  conversationMessages: (id: string) =>
    apiGet<ConversationMessage[]>(`/api/chat/conversations/${id}/messages`),
  renameConversation: (id: string, title: string) =>
    apiSend(`/api/chat/conversations/${id}/title`, "PUT", { title }),
  deleteConversation: (id: string) => apiSend(`/api/chat/conversations/${id}`, "DELETE"),

  files: {
    upload: uploadFile,
    mine: () => apiGet<StoredFileInfo[]>("/api/files/mine"),
    downloadUrl: (id: string) => `${API_BASE}/api/files/${id}`,
  },

  // The current user's in-app notification inbox (strictly self-scoped server-side).
  notifications: {
    list: (unreadOnly = false) =>
      apiGet<NotificationInfo[]>(`/api/notifications/${unreadOnly ? "?unreadOnly=true" : ""}`),
    markRead: (id: string) => apiSend(`/api/notifications/${id}/read`, "POST"),
    markAllRead: () => apiSend("/api/notifications/read-all", "POST"),
  },

  // Human-in-the-loop: side-effecting tool calls the agent was blocked from auto-running.
  approvals: {
    list: () => apiGet<PendingApproval[]>("/api/chat/approvals"),
    approve: (id: string) => apiSend(`/api/chat/approvals/${id}/approve`, "POST"),
    reject: (id: string) => apiSend(`/api/chat/approvals/${id}/reject`, "POST"),
  },

  connectors: {
    // The tenant-enabled DELEGATED connectors with whether the current user has linked their
    // account — the data behind the Connected accounts page.
    list: () => apiGet<UserConnector[]>("/api/connectors"),
    // Any authenticated user starts THEIR OWN account link for a delegated connector (stage 2 of
    // enablement); the returned authorizeUrl opens the IdP's consent page in a new tab.
    oauthStart: (connectorId: string) =>
      apiGet<{ authorizeUrl: string }>(`/api/connectors/${encodeURIComponent(connectorId)}/oauth/start`),
    // Unlink the current user's own account (removes our stored tokens).
    disconnect: (connectorId: string) =>
      apiSend(`/api/connectors/${encodeURIComponent(connectorId)}/login`, "DELETE"),
  },

  admin: {
    securityCatalog: () => apiGet<SecurityCatalog>("/api/admin/security/catalog"),
    roles: () => apiGet<RoleInfo[]>("/api/admin/roles"),
    setRolePermissions: (role: string, permissions: string[]) =>
      apiSend(`/api/admin/roles/${encodeURIComponent(role)}/permissions`, "PUT", { permissions }),
    createRole: (role: string, permissions: string[]) =>
      apiSend("/api/admin/roles", "POST", { role, permissions }),
    deleteRole: (role: string) =>
      apiSend(`/api/admin/roles/${encodeURIComponent(role)}`, "DELETE"),
    users: () => apiGet<AdminUser[]>("/api/admin/users"),
    assignRole: (userId: string, role: string) =>
      apiSend(`/api/admin/users/${userId}/roles`, "POST", { role }),
    revokeRole: (userId: string, role: string) =>
      apiSend(`/api/admin/users/${userId}/roles/${encodeURIComponent(role)}`, "DELETE"),
    grantPermission: (userId: string, permission: string) =>
      apiSend(`/api/admin/users/${userId}/permissions`, "POST", { permission }),
    revokePermission: (userId: string, permission: string) =>
      apiSend(`/api/admin/users/${userId}/permissions/revoke`, "POST", { permission }),
    setUserActive: (userId: string, isActive: boolean) =>
      apiSend(`/api/admin/users/${userId}/active`, "PUT", { isActive }),
    auditToolCalls: (take = 100) =>
      apiGet<ToolCall[]>(`/api/admin/audit/tool-calls?take=${take}`),
    auditAuthEvents: (take = 100) =>
      apiGet<AuthEvent[]>(`/api/admin/audit/auth-events?take=${take}`),
    modules: () => apiGet<ModuleAdmin[]>("/api/admin/modules"),
    setModuleEnabled: (moduleId: string, enabled: boolean) =>
      apiSend(`/api/admin/modules/${encodeURIComponent(moduleId)}`, "PUT", { enabled }),
    connectors: () => apiGet<ConnectorAdmin[]>("/api/admin/connectors"),
    setConnectorEnabled: (connectorId: string, enabled: boolean) =>
      apiSend(`/api/admin/connectors/${encodeURIComponent(connectorId)}/${enabled ? "enable" : "disable"}`, "POST"),
    // Omitted keys keep their stored value (the UI never has a secret to echo back).
    setConnectorSettings: (connectorId: string, values: Record<string, string>) =>
      apiSend(`/api/admin/connectors/${encodeURIComponent(connectorId)}/settings`, "PUT", { values }),
    tenants: () => apiGet<AdminTenant[]>("/api/admin/tenants"),
    createTenant: (name: string, slug: string) =>
      apiSend("/api/admin/tenants", "POST", { name, slug }),
    setTenantActive: (tenantId: string, isActive: boolean) =>
      apiSend(`/api/admin/tenants/${tenantId}/active`, "PUT", { isActive }),
    aiSettings: () => apiGet<AiSettings>("/api/admin/ai-settings"),
    setAiSettings: (settings: {
      systemPrompt: string | null;
      maxConversationTokens: number | null;
      maxMonthlyTokens?: number | null;
      /** Null = deployment default provider. */
      provider?: string | null;
      model?: string | null;
      endpoint?: string | null;
      /** Write-only: undefined/null = keep the stored key, non-empty = replace, "" = clear. */
      apiKey?: string | null;
    }) => apiSend("/api/admin/ai-settings", "PUT", settings),
    agentProfiles: (moduleId?: string) =>
      apiGet<AgentProfile[]>(
        `/api/admin/agent-profiles${moduleId ? `?moduleId=${encodeURIComponent(moduleId)}` : ""}`,
      ),
    upsertAgentProfile: (profile: {
      moduleId: string;
      name: string;
      instructions: string;
      mode: "Append" | "Replace";
      isDefault: boolean;
      toolNames?: string[] | null;
      model?: string | null;
    }) => apiSend("/api/admin/agent-profiles", "PUT", profile),
    deleteAgentProfile: (id: string) => apiSend(`/api/admin/agent-profiles/${id}`, "DELETE"),
    usage: (days = 30) => apiGet<UsageReport>(`/api/admin/usage?days=${days}`),
    ops: () => apiGet<OpsSnapshot>("/api/admin/ops"),
    notificationSettings: () => apiGet<NotificationSettings>("/api/admin/notification-settings"),
    setNotificationSettings: (settings: {
      webhookUrl: string | null;
      /** Write-only: undefined/null = keep the stored secret, non-empty = replace, "" = clear. */
      webhookSecret?: string | null;
    }) => apiSend("/api/admin/notification-settings", "PUT", settings),
  },
};
