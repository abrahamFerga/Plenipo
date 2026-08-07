# Plenipo architecture

This is the map of how Plenipo fits together — read it after [GETTING_STARTED.md](GETTING_STARTED.md) if you
want to understand the codebase or build a vertical on it.

## What Plenipo is

Plenipo is a **base platform for AI-first, chat-first products**. The thesis: every "AI app for industry X"
needs the same backbone — a chat assistant with tools, tenant isolation, role-based access, an audit trail,
cost tracking, and a dashboard — and differs only in its *domain* (the tools, data, and instructions). So
Plenipo makes the backbone a reusable platform and the domain a **module**.

- The platform ships as **NuGet packages** (backend) and an **npm library** (frontend).
- A product is a thin **host** that installs one or more modules; it is not a fork of the platform.
- A **module** implements one interface (`IModule`) and declares its capabilities up front (manifest-first).

The headline design choice is **tool security before the model call**: the agent runner filters tools by the
caller's permissions *before* building the LLM request, so the model never even sees the schema of a tool the
user isn't allowed to invoke. Side-effecting tools are gated further by **human-in-the-loop approval**.

## The big picture

```mermaid
flowchart TD
  subgraph platform["Base platform — publishable packages"]
    Core["Plenipo.Core<br/>entities, multi-tenancy, results"]
    Sdk["Plenipo.Modules.Sdk<br/>IModule, ModuleManifest, ToolDescriptor"]
    App["Plenipo.Application<br/>RBAC, agent + audit contracts, AI options"]
    Infra["Plenipo.Infrastructure<br/>EF Core, agent runner, AI providers, audit"]
    Web["Plenipo.AspNetCore<br/>auth, endpoints, SignalR, AG-UI, module host"]
    SD["Plenipo.ServiceDefaults<br/>Aspire: OTel, health, resilience"]
  end

  Core --> Sdk --> App --> Infra --> Web
  SD --> Web

  Module["A domain module<br/>(Finance / Nutrition / Legal / yours)"]
  Module -. implements .-> Sdk
  Host["Your API host<br/>(samples/Plenipo.Sample.Host)"]
  Host --> Web
  Host -. "AddPlenipoModule" .-> Module
  Client["@plenipo/client<br/>renderer-free contract + REST + AG-UI"]
  UI["@plenipo/ui (React)<br/>domain shell"] -- "/api/platform/modules" --> Host
  Mobile["@plenipo/mobile (React Native)<br/>the same manifest, natively"] -- "/api/platform/modules" --> Host
  Admin["@plenipo/admin-ui<br/>admin console @ /admin"] -- "/api/admin/*" --> Host
  Client --> UI
  Client --> Mobile
```

## A chat turn, end to end

This is the security spine — the most important flow to understand.

```mermaid
sequenceDiagram
  actor User
  participant API as "API (AG-UI / SignalR / REST)"
  participant Runner as AuthorizedAgentRunner
  participant Agent as "MAF agent (OTel-traced)"
  participant Tools as "Module tools (middleware)"
  participant DB as "Audit + platform DBs"

  User->>API: chat message (moduleId, text)
  API->>Runner: RunAsync(request)
  Runner->>Runner: resolve module manifest
  Runner->>Runner: filter tools by caller permissions
  Note over Runner: the model NEVER sees a tool<br/>the user may not call
  Runner->>DB: resolve conversation, load its history (resume)
  Runner->>Runner: enforce per-conversation token budget
  Runner->>Agent: build agent (instructions + permitted tools)
  Agent->>Tools: invoke a tool
  alt read-only tool
    Tools->>DB: audit the call, return result
  else side-effecting (RequiresApproval)
    Tools->>DB: block + record a PendingApproval
    Tools-->>Agent: "needs approval, not executed"
  end
  Agent-->>Runner: stream tokens + token usage
  Runner->>DB: persist turn + token usage
  Runner-->>API: stream events (tokens, tool notices, usage, approval)
  API-->>User: streamed response
```

The same `AuthorizedAgentRunner` backs all three transports: the open **AG-UI** protocol
(`POST /api/agui/{moduleId}`, SSE), **SignalR** (`/hubs/agent`), and a **REST** stream
(`POST /api/chat/stream`). RBAC filtering, auditing, token tracking, and approval all apply regardless.

## The module system

A vertical plugs in through one interface — declared statically, so the platform can reason about
capabilities (navigation, permissions, audit policy) without running module code.

```csharp
public interface IModule
{
    ModuleManifest Manifest { get; }                              // tools, tabs, roles, agent instructions
    void RegisterServices(IServiceCollection services, IConfiguration config);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
    Task MigrateAsync(IServiceProvider services, CancellationToken ct = default);  // optional: own DB
    Task SeedAsync(IServiceProvider services, CancellationToken ct = default);     // optional
}
```

- The **manifest** lists `ToolDescriptor`s (name, description, the permission the tool requires, and whether
  it `RequiresApproval`) and `TabDescriptor`s (which drive the dashboard navigation, server-side).
- An `IModuleToolSource` supplies the *executable* tools (`AIFunction`s bound to permissions).
- A module may own persistence (its own `DbContext` + schema, migrated via `MigrateAsync`) or be stateless.

The three sample verticals show the spectrum: **Finance** (stateful — learns categories, budgets, HITL
transaction recording), **Nutrition** and **Legal** (stateless reference data + drafting). The host installs
them with `builder.AddPlenipoModule<FinanceModule>()`.

## Data & multi-tenancy

```mermaid
flowchart LR
  subgraph platformdb["Platform database"]
    ps["platform schema<br/>tenants, users, roles, grants,<br/>role_permission_suppressions,<br/>conversations, pending_approvals"]
    fs["finance schema<br/>transactions, budgets, rules<br/>(a module owns this)"]
  end
  subgraph auditdb["Audit database (append-only)"]
    aud["audit schema<br/>agent_runs, tool_calls, auth_events,<br/>entity_changes, token_usage"]
  end
```

Every tenant-owned entity implements `ITenantOwned`; EF Core **global query filters** on `TenantId` are
applied automatically by reflection, so no query — including one written by module code — can cross a tenant
boundary. The audit store is a separate database (separate credential in production) that application code
only ever appends to. In production the platform and audit databases are distinct (Terraform provisions
both); for local dev they share one Postgres via distinct schemas.

Audit writes are **durable**: the audit log writes straight to the audit store (synchronous, immediately
queryable), but if that write fails — a transient audit-DB outage — the record is serialized to an
`audit_outbox` table in the platform DB and a background `AuditOutboxProcessor` flushes it once the audit
store recovers. So a momentary outage defers audit records instead of dropping them, and the "audit
everything" guarantee survives an audit-DB blip without ever failing the user-facing operation.

**Every agent turn is on the record.** `agent_runs` holds exactly one row per turn — completed, refused,
blocked, over budget, thrown, or abandoned mid-stream — written from a `finally` in `AuthorizedAgentRunner`
so no early exit can skip it. This is deliberately separate from `token_usage`, which a provider only
reports for a turn it actually billed: a turn refused before the model is reached costs nothing and would
otherwise leave no trace at all, and those are exactly the turns an operator is looking for. Each row
carries its outcome, the internal error kind and message (not the sanitized text the user saw), latency
including time-to-first-token, the effective provider and model, the instructions hash, and the trace id
that joins back to the OpenTelemetry span while that trace is still retained. Admin → Agent Runs is the
read surface, gated on `platform.audit.view`.

## Security model

Three layers, evaluated by `PermissionMatcher` (supports `*` and dotted wildcards like `tools.finance.*`):

1. **System roles** — `system_admin`, `tenant_admin`, `user`, `guest`. What a role grants is
   `(baseline ∖ suppressed) ∪ granted`: the **declared baseline** (`RolePermissions.Defaults` merged with the
   host's `AddPlenipoRole` declarations) adjusted by the tenant's **deviations** — the permissions it added
   (`role_permissions`) and the ones it removed (`role_permission_suppressions`), both edited in the admin
   console. A tenant stores only what it *changed*, never the whole set, so a permission a product declares
   reaches every tenant immediately — including one provisioned long before the declaration changed — while a
   permission an admin removed stays removed across every later baseline change. `PermissionResolver` expands
   a principal's roles through this (see `RolePermissionResolution` / `RoleGrants`, and
   [ADR 0002](docs/adr/0002-role-permission-deviation-storage.md)). `system_admin` always resolves to `*`
   regardless of stored rows — a lockout guardrail, and the role is rejected by the edit endpoint.
2. **Feature permissions** — dotted strings (`tools.finance.record_transaction`, `platform.users.manage`,
   `chat.approvals.manage`). Endpoints gate on these via a dynamic `IAuthorizationPolicyProvider`.
3. **Per-resource ACLs** — owner/editor/viewer (the seam exists; module-specific).

On top of that: **pre-model-call tool filtering** (the agent only receives tools the caller can invoke),
**human-in-the-loop approval** for side-effecting tools (blocked → recorded → an authorized human approves,
which re-executes the exact recorded call), and **dual-database audit** of every tool call, data change, and
token spend. The **admin console** surfaces all of this — and the runtime configuration around it: the role
editor with the live permission map, users, per-tenant module and connector enablement, tenants, AI
settings (runtime provider switching with vaulted keys), agent profiles (per-agent instructions,
tools, and model), token usage, the audit log, and an operations snapshot.

The LLM-backed chat endpoints (`/api/chat/stream`, `/api/agui/{moduleId}`) are additionally **rate-limited
per user** — a fixed window partitioned by the caller's `sub` claim, a cost/abuse backstop so one principal
can't exhaust the model budget or starve others. The limit is configurable (`RateLimiting:ChatPermitsPerMinute`,
generous by default); over-limit requests get `429` + `Retry-After`. A deactivated **user** — or every user
of a deactivated **tenant** — keeps a valid token but is denied every request (enforced in the
request-enrichment middleware and the SignalR hub filter): per-user and tenant-wide kill switches an operator
drives from the admin console.

## Observability

`Plenipo.ServiceDefaults` wires OpenTelemetry (ASP.NET Core + HttpClient + runtime). The agent pipeline is
additionally instrumented under the **`Plenipo.Agents`** source, so agent runs and LLM calls (with token
usage) surface in the **Aspire dashboard** alongside HTTP and database activity.

## Frontend

The frontend is **three surfaces over one contract**. The contract is
**`@plenipo/client`** — a renderer-free TypeScript package holding the mirror of every C# descriptor
(`ModuleManifest`, `TabDescriptor`, …), the REST surface, the AG-UI chat transport, the
`PermissionMatcher` mirror, and the chart shaping. It imports no React and touches no DOM, so a
change to the C# side lands in exactly one TypeScript file and every shell sees it. (Enforced, not
merely intended: a test reads the sources and fails on `document.`, `window.`, `localStorage`,
`import.meta.env`, or an import of `react`/`react-native`.)

The three surfaces are deliberately separated so the product UI can be adapted/branded per host
while operator administration stays generic and consistent across every deployment:

- **`@plenipo/ui`** — the **end-user / domain** shell (a React + Vite library). It is **server-driven**: it
  builds the module switcher, tabs, and routes entirely from `GET /api/platform/modules`, so installing a
  backend module automatically adds its UI. It talks to the agent over SignalR (and ships an AG-UI client)
  and shows per-turn token usage. Domain-specific UI ships as separate packages that depend on it; the base
  library carries no vertical-specific and no admin code. It also re-exports the API/auth client layer
  (`api`, `useMe`, `hasPermission`, admin types) that the admin console consumes.
- **`@plenipo/admin-ui`** — the **admin console** (a standalone app, not a library): the security map, a
  **schema-driven role editor** (every permission toggle derived from the live catalog, so a new module's
  tools appear with no UI change — plus a free-text escape hatch for wildcards), users & grants,
  token-usage, and audit views. It reuses `@plenipo/ui`'s client layer and is served at
  `/admin` — by its own Vite dev server in development, or by the API host in an integrated deployment via
  `app.UsePlenipoAdminConsole()` (Plenipo's analogue of OpenClaw's "control UI built into the gateway"). The
  console is just static assets; the `/api/admin/*` endpoints it reads stay RBAC-gated server-side, so the
  API — not the asset host — remains the security boundary.
- **`@plenipo/mobile`** — the **native** end-user shell (React Native / Expo). The same idea as
  `@plenipo/ui`, rendered with native views: it builds the tab bar, tables, editor forms, charts,
  detail views and chat from the same `/api/platform/modules` payload, so **installing a backend
  module puts it on phones that already have the build** — a backend deploy, not an App Store
  review. A product's app is a base URL, a brand, and (optionally) native screens registered per
  tab through the same `defineModule` registry the web uses.
  <br>Chat rides the **AG-UI** SSE endpoint rather than the SignalR hub — a WebSocket on a phone
  dies on every backgrounding and hand-off — but both drive the same `AuthorizedAgentRunner`, so
  RBAC filtering, approvals, audit and token accounting are identical. Push notifications reach it
  through the ordinary `INotificationChannel` seam (`PushNotificationChannel` + a pluggable
  `IPushTransport`), which makes a phone the fastest way to clear the human-in-the-loop approval
  queue. See [docs/MOBILE.md](docs/MOBILE.md).

## Where to look in the code

| Concern | Start here |
|---------|-----------|
| Chat security spine | `src/Plenipo.Infrastructure/Agents/AuthorizedAgentRunner.cs` |
| Tool audit + approval gate | `src/Plenipo.Infrastructure/Agents/ToolInvocationMiddleware.cs` |
| Module contract | `src/Plenipo.Modules.Sdk/IModule.cs`, `ModuleManifest.cs` |
| RBAC | `src/Plenipo.Application/Authorization/` |
| Multi-tenancy | `src/Plenipo.Infrastructure/Persistence/PlatformDbContext.cs` |
| Endpoints (platform/chat/admin/approvals/AG-UI) | `src/Plenipo.AspNetCore/Endpoints/` |
| Serving the admin console at `/admin` | `src/Plenipo.AspNetCore/Hosting/AdminConsoleExtensions.cs` |
| Shared frontend contract | `frontend/plenipo-client/` (`@plenipo/client`) |
| Domain (end-user) UI | `frontend/plenipo-ui/` (`@plenipo/ui`) |
| Mobile shell + reference app | `frontend/plenipo-mobile/` (`@plenipo/mobile`), `frontend/mobile-app/` |
| Admin console app | `frontend/admin-ui/` (`@plenipo/admin-ui`) |
| A worked example module | `samples/Plenipo.Modules.Finance/` |
| Infra (Azure) | `infra/` (Terraform) + `.github/workflows/` |
