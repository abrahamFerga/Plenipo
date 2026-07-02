# Changelog

All notable changes to Cortex are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); Cortex will adopt
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) once it leaves alpha.

Releases are cut from a tagged GitHub Release (`v*`), which triggers the publish workflow
(`.github/workflows/publish.yml`) to push the `Cortex.*` NuGet packages and the `@cortex/ui`
npm package. Until then, everything lives under **Unreleased**.

## [Unreleased] — toward 0.1.0-alpha

The first alpha: the base platform, an admin/security dashboard, and three sample verticals,
all runnable with no AI key via a built-in Mock provider. See [README.md](README.md) and
[GETTING_STARTED.md](GETTING_STARTED.md).

### Added

**Platform (backend NuGet packages)**
- **Module SDK** — a vertical implements `IModule` and declares a `ModuleManifest` (tools, tabs,
  roles, agent instructions); the host discovers and installs it with `AddCortexModule<T>()`.
  See [BUILDING_A_MODULE.md](BUILDING_A_MODULE.md).
- **Chat-first agent pipeline** on Microsoft Agent Framework over `Microsoft.Extensions.AI`, streamed
  over SignalR (Redis backplane) and the open **AG-UI** protocol.
- **Tool security before the model call** — the agent runner filters tools by the caller's permissions
  before building the request, so the LLM never sees a tool the user may not call.
- **Human-in-the-loop approvals** — side-effecting tools are blocked pending explicit approval.
- **Layered RBAC** (system roles → dotted permissions with wildcards → per-resource ACLs), an
  append-only **audit log**, and per-turn **token-usage** tracking.
- **Multi-tenant by default** — row-level isolation via EF Core global query filters on `TenantId`.
- **External-IdP authorization mode** — `Auth:PermissionSource=Token` makes Entra External ID / B2C
  the single source of truth: roles come exclusively from the token, internal role assignments and
  per-user grants are ignored (their admin endpoints answer 409 with guidance), and JIT provisioning
  never invents a default role. Role → permission baselines remain the translation layer from IdP
  role names to fine-grained tool permissions.
- **Provider-swappable AI** (OpenAI / Azure OpenAI / Ollama) plus a dependency-free **Mock** provider so
  chat — and real, audited tool calls plus the approval gate — work with zero configuration.
- **Admin/security dashboard API** — the full permission map, users & roles, token usage, and audit log.
- **MAF agent sessions** — conversations persist and resume via `AgentSession` state on the
  conversation row, so multi-turn context survives restarts and channel hops.
- **Platform document tools** — every module's agent can read PDFs (PdfPig), generate PDFs, list
  files, and OCR (pluggable `IOcrEngine` seam), over a tenant-scoped **file store** (local disk or
  Azure Blob). Module code gets the same via `IDocumentReader`/`IPdfRenderer`. The pack is
  switchable per deployment (`Documents:Enabled`, on by default) on top of the per-tenant
  (role-baseline) and per-user (permission) gates.
- **WhatsApp channel** (Meta Cloud API) — HMAC-verified webhook, JIT phone-user provisioning,
  inbound media into the file store, per-tenant module binding; off by default, keyless E2E tests.
- **Background jobs** — modules enqueue long-running work (`IJobQueue`/`IJobHandler`); the processor
  restores the enqueuer's tenant/user/permissions (capability capture) so RBAC, filters, and audit
  hold inside jobs. Claim **leases** recover jobs orphaned by a crashed host (requeue up to 3
  attempts, then fail); running jobs **cancel cooperatively** at progress reports, and only their
  enqueuer may cancel them. Pollable at `/api/jobs`.
- **Separate-systems model** — each vertical is its own product/host/repo on the platform packages
  (`samples/Cortex.Legal.Host` is the canonical single-vertical shape); systems connect via the
  **cortex-peer connector**, which lets one deployment's agent ask another's over the open AG-UI
  protocol (the peer enforces its own auth, RBAC, tool gating, and audit; credential is a protected
  secret).
- **Data-source connectors** — a manifest-first **connector SDK** (`Cortex.Connectors.Sdk`:
  `IConnector`, settings schema, tool source) bridging agents to where tenant data already lives.
  Connectors are **default-off per tenant**: an admin enables and configures each one on the new
  **Integrations** page (`/api/admin/connectors`); only then do its tools exist for that tenant's
  agents (still permission-gated per tool, fetches approval-gated, everything audited). Secret
  settings are write-only and protected at rest. Ships with **Azure Blob Storage** and a keyless
  **local-folder** connector; fetched files land in the tenant file store, so attachments, document
  tools, matters, and RAG indexing work on them unchanged.
- **Delegated connectors (per-user OAuth)** — the two-stage model completed: the admin enables and
  registers the IdP app (stage 1), each USER connects their own account through a real
  auth-code+PKCE flow (stage 2); tokens are stored protected and refresh transparently, and
  **disabling a connector revokes every user session**. Ships the **Microsoft 365 connector**
  (`msgraph`): browse/fetch OneDrive/SharePoint on the user's own token, so Microsoft enforces
  their permissions per call.
- **Connector sync (Lane B)** — bind ONE external folder to ONE module resource (Harvey-style
  scoped bindings, never global indexing): a background sync job imports new/changed files
  (incremental via per-item stamps) into the file store and hands them to the owning module. In
  Legal, `connect_matter_folder` / `sync_matter_folder` attach synced files to the matter AND index
  them into its knowledge collection — "keep this matter in sync with our folder" ends in cited,
  searchable knowledge.
- **`cortex` CLI** — `cortex init` (interactive wizard or `--non-interactive` flags) writes one
  declarative `cortex.settings.json` the host layers into configuration; re-runs are
  non-destructive and secrets are never written (user-secrets commands are printed instead).
- **Permission-aware RAG** (opt-in, `Rag:Enabled`) — documents ingest into **scoped collections**
  (per matter/project, the Harvey-Vault pattern) via a background job; retrieval is **hybrid**
  (pgvector + tsvector fused with RRF, tenant/collection predicates in both arms) through the
  `search_knowledge` platform tool, with per-passage file citations. Access to a resource-bound
  collection goes through the owning module's `IRagCollectionGate` and **fails closed**. Embeddings
  ride the swappable `IEmbeddingGenerator` seam — a deterministic **Mock embedder** keeps the whole
  pipeline keyless in dev/CI. Requires pgvector (dev/CI images updated). See
  [docs/PLATFORM_CONNECTORS_RAG_PLAN.md](docs/PLATFORM_CONNECTORS_RAG_PLAN.md).

**Frontend (`@cortex/ui`, `@cortex/admin-ui`)**
- React 18 + Vite libraries: the chat shell (attachments, streaming, retry, approvals), module
  switcher, server-driven data tabs, and the separate admin console. Ships ESM + UMD bundles with
  bundled TypeScript declarations.
- **Dark mode** — a light/dark/system toggle in both app headers; the preference persists, "system"
  follows the OS live, and a pre-bundle guard prevents a light flash on reload.
- **Chat speaks AG-UI by default** — the chat panel now drives the open AG-UI protocol
  (POST /api/agui/{module} + SSE) end to end; SignalR remains available via `transport="signalr"`.
  A thread id that is an existing conversation's id resumes it server-side, so picking a
  conversation from history continues it over AG-UI.
- **Drag & drop + paste attachments** — drop files anywhere on the chat panel (with a drop overlay)
  or paste a file into the composer; both upload to the file store and attach as chips.
- **Connect account** — the Integrations page shows a per-user "Connect account" button on enabled
  delegated connectors, opening the IdP consent page from /oauth/start.

**Samples**
- Three demo verticals — **Finance** (rule-based categorizer + LLM fallback, budgets, seeded demo ledger),
  **Nutrition**, **Legal** — plus a minimal **Tasks** template that backs the build-a-module tutorial.
- The **Legal** vertical grew into the flagship demo: matter workspaces, attach-document-to-matter,
  cited Q&A over matter documents, a tenant clause library + negotiation playbook, a prescribed
  drafting chain (draft → PDF → file on the matter), playbook contract review, a job-backed **bulk
  review table** (documents × questions with verbatim, cited excerpts), WhatsApp client intake,
  **matter knowledge search** (`index_matter_documents` → `search_knowledge` over the matter's RAG
  collection), and **ethical walls** (`restrict_matter_access` — a walled matter vanishes from every
  tool, tab, and its knowledge collection for everyone outside the wall, wildcard permissions or not).

**Tooling & ops**
- **.NET Aspire** AppHost (Postgres + Redis + API + both UIs as Vite resources + a live telemetry
  dashboard) and `docker compose` for the quickstart.
- **Terraform** (Azure Container Apps, Postgres, Redis, Key Vault, Entra External ID) and **GitHub
  Actions** (CI, deploy, publish to GitHub Packages) + Trivy scanning + Dependabot.
- **Tests** — 300+ .NET (unit + Testcontainers integration, all keyless via the Mock provider),
  120+ frontend vitest unit/component tests, and Playwright browser E2E specs.
- **Docs** — README, GETTING_STARTED, BUILDING_A_MODULE, ARCHITECTURE, CONTRIBUTING, SECURITY,
  WHATSAPP_CHANNEL, DOCUMENT_TOOLS, LEGAL_VERTICAL_PLAN, PLATFORM_CONNECTORS_RAG_PLAN.
