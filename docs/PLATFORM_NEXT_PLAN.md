# Platform next wave — deployments, agent profiles, skills, secrets

Directive (2026-07-04): efficient deployments (Terraform, Azure-centric, multi-cloud-friendly,
learn from OpenClaw); configurable chatbots with different instructions; MAF agent-skills
support; secrets that non-technical users can configure through our UI (Key Vault-grade at
rest); plus new ideas.

Loop protocol: one phase per pass — implement, build, test, commit, mark the checkbox, yield.

## Research notes

**OpenClaw's deployment model** is deliberately boring, and that's the lesson: one Docker
Compose file, ONE mounted state directory holding everything the system cares about (configs,
credentials, memory index), `docker compose pull && docker compose up -d` as the whole upgrade
story, and pinned version tags so updates are deliberate. Production setups converge on Docker
because it gives isolation + easy updates + portable state.
Cortex equivalents: all state already externalized (Postgres + DataProtection keys), so the
gaps are (a) a published container image + compose file, (b) Terraform for the Azure resources,
(c) pinned-tag upgrade docs.

**MAF agent skills** (`AgentSkillsProvider`, experimental `MEAI001`): file-based `SKILL.md`
bundles (frontmatter name/description + instructions + `references/` + `scripts/`) loaded via
progressive disclosure — the prompt carries only name+description; the model calls
`load_skill` / `read_skill_resource` / `run_skill_script` on demand. `UseScriptApproval(true)`
wraps script execution in the same approval flow Cortex already renders. Scripts are
unsandboxed subprocesses — approval gate is mandatory, and skills directories must be
deploy-time content, not tenant uploads, until sandboxing (Hyperlight) is evaluated.

**Secrets today**: connector secrets are already write-only, DataProtection-encrypted at rest,
editable by non-technical admins in the connector UI. The ask is a pluggable backend so the
same UI can persist to Azure Key Vault instead of the DB (and AI provider keys can come from
KV references instead of env vars).

## Phases

- [x] **Phase 0 — this plan** (research + document).
- [x] **Phase 1 — Agent profiles (configurable chatbots)**: `AgentProfile` entity
  (tenant + module scoped, named, `Instructions`, mode append|replace, one default per
  module), admin CRUD under `/api/admin/agent-profiles` (permission `platform.ai.manage`),
  runner resolves the module's default profile each turn and composes instructions
  (pure `InstructionComposer`, unit-tested). Delivered: commit <this pass>.
- [x] **Phase 2 — Secret provider abstraction**: `ISecretVault` seam behind connector settings
  AND per-user OAuth tokens. `DataProtectionSecretVault` (default; accepts legacy bare
  ciphertext) and `KeyVaultSecretVault` (`Secrets:Provider=AzureKeyVault` +
  `Secrets:KeyVaultUri`; values live in KV, the DB keeps `kv:{name}` pointers;
  `DefaultAzureCredential`). References are prefix-tagged, so switching providers needs no
  migration — old secrets keep resolving, new writes land in the new backend. Replaced/cleared
  secrets are forgotten from KV best-effort. Same write-only admin UI. Delivered.
  Deferred to a later pass: resolving `Ai:ApiKey` through the vault (needs the vault before the
  ChatClient singleton is built — a small startup-ordering refactor).
- [x] **Phase 3 — Agent skills (MAF file format, Cortex pipeline)**: `Skills:Enabled` +
  `Skills:Path`; `FileSkillCatalog` loads `SKILL.md` bundles (same on-disk format as MAF
  file-based skills / agentskills.io: frontmatter name+description, instruction body,
  `references/`, `scripts/`). DESIGN DECISION: the progressive-disclosure loop
  (`load_skill` / `read_skill_resource` / `run_skill_script`) ships as platform tools
  (`tools.skills.*`) rather than MAF's provider-injected functions, so RBAC filtering, tool-call
  audit, the approval flow, AND approved-script re-execution through ApprovalExecutor all work
  unchanged — MAF's `UseScriptApproval` would have created a second, parallel approval pipeline
  the admin UI can't see. `<available_skills>` advertisement appended to instructions only when
  the user can call load_skill. Script runs: interpreter by extension (py/js/sh/ps1), skill-dir
  confinement (traversal rejected), timeout + process-tree kill, approval-gated. Sample skill
  `samples/Cortex.Sample.Host/skills/brand-voice`. Delivered.
  ALSO FIXED (found during integration): connector tools' `RequiresApproval` flag was shown in
  the admin UI but never enforced at run time — the runner's approval set only included module
  manifest tools. Now unioned with per-ModuleTool flags; ApprovalExecutor also falls back to the
  connector catalog so approved connector tools actually re-execute.
- [x] **Phase 4 — Deployment: containers + compose (the OpenClaw lesson)**: multi-stage
  Dockerfile for `samples/Cortex.Sample.Host` (modules + connectors + sample skill baked in,
  non-root runtime), `deploy/compose/` — docker-compose.yml (api + pgvector pg17 + redis, all
  pinned, named volumes as the single state boundary, healthchecks, audit-DB init script),
  `.env.example` (one required value), README covering quickstart / `pull && up -d` upgrades /
  pg-major caveat / backup = pg_dumpall + .env / going-real table. Keyless demo works out of
  the box (Development + Mock); Production requires an IdP, stated loudly. Delivered.
  Follow-up idea recorded: a bundled UI container (nginx serving the built front-ends).
- [x] **Phase 5 — Deployment: Terraform Azure**: the `infra/` tree already covered the
  topology (Container Apps + Flexible Postgres + Redis + Key Vault + Log Analytics +
  Entra External ID + OIDC CI identity), so this phase closed the gaps against the new
  platform features: **pgvector allowlisted** (`azure.extensions=VECTOR` — RAG migrations
  would have failed in Azure without it) and Postgres bumped to **pg17** (the verified
  pairing); new `enable_keyvault_secret_vault` toggle wires the Phase 2 vault (app identity
  gets Secrets Officer, `Secrets__Provider`/`Secrets__KeyVaultUri` env injected). Validated
  with `terraform fmt -check` + `init/validate` (in a hashicorp/terraform container — no
  local CLI); provider lock file committed. Multi-cloud stance unchanged: compose is the
  cloud-neutral path; per-cloud Terraform trees, not a leaky abstraction.
- [ ] **Phase 6 — New ideas backlog** (grow as they land):
  - [x] **Org (monthly) token budgets + admin alerts**: `MaxMonthlyTokens` (deployment default
    in `Ai:` + per-tenant override in AI settings; null inherits, 0 unlimited). The runner
    refuses turns tenant-wide once the UTC-month total reaches the cap, and alerts the tenant's
    admins through the notification seam on the crossing turn — 80% warning, exhaustion notice
    (single alert when one turn crosses both). Recipients = role-row tenant admins ∪ the acting
    user when they hold platform.ai.manage (covers Token authorization mode, where no role rows
    exist). Delivered.
  - [x] **Eval harness** (docs/EVALS.md): golden-conversation evals as JSON data under
    `samples/Cortex.Sample.Host.IntegrationTests/Evals/cases/` — one user turn + the
    behavioral contract (tools routed, approval gate fired, reply must/mustn't say).
    Runs through the REAL pipeline (auth, RBAC filtering, approval, audit, Mock provider)
    via the AG-UI stream; protocol health asserted on every case; add a case = drop a JSON
    file. 5 seed cases incl. the approval contract and the skills loop. Delivered.
  - [x] **Prompt provenance**: every assistant message is stamped with the SHA-256 of the
    effective instruction assembly it ran under (system prompt + manifest + profile + skills
    advertisement); distinct assemblies are recorded once per tenant in `instruction_snapshots`,
    resolvable via GET `/api/admin/instruction-snapshots/{hash}` (platform.ai.manage).
    Best-effort by design — provenance failure never fails a chat turn. Delivered.
  - [x] **Notification seam (in-app baseline + channel interface)**: `INotifier` persists a
    durable in-app inbox row (`user_notifications`) then fans out best-effort to registered
    `INotificationChannel`s (none by default — webhook/email are follow-ups). Self-scoped user
    API: GET `/api/notifications` (unread-first), mark-read, read-all. First producer: the job
    processor notifies the enqueuer on Succeeded/Failed (best-effort, never disturbs job state).
    Explicit tenant/user on the Notification record — producers run outside request scopes.
    Delivered — and the webhook channel followed: per-tenant URL + write-only signing secret
    (ISecretVault, scope Cortex.Notifications.WebhookSecret), payloads HMAC-signed
    (X-Cortex-Signature: sha256=…, GitHub/Meta scheme), admin surface
    /api/admin/notification-settings under the new platform.notifications.manage permission.
    Explicit-tenant config lookup (producers have no ambient tenant). Also fixed in passing:
    tools.skills.* now appear in the permission catalog and the tenant_admin baseline.
    Remaining follow-ups: calendar-reminder producer (module-side), UI bell/badge in @cortex/ui.
  - **Cross-module handoff**: MAF handoff workflow between module agents ("ask finance" from
    legal chat) — the cortex-peer connector already covers the cross-system case.
  - **Admin ops tab**: job queue depth, connector sync health, RAG index freshness in one view.
