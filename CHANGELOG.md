# Changelog

All notable changes to Plenipo are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); Plenipo will adopt
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) once it leaves alpha.

Releases are cut from a tagged GitHub Release (`v*`), which triggers the publish workflow
(`.github/workflows/publish.yml`) to push the `Plenipo.*` NuGet packages and the `@plenipo/client`
and `@plenipo/ui` npm packages. Until then, everything lives under **Unreleased**.

## [Unreleased] — toward 0.1.0-alpha

The first alpha: the base platform, an admin/security dashboard, and three sample verticals,
all runnable with no AI key via a built-in Mock provider. See [README.md](README.md) and
[GETTING_STARTED.md](GETTING_STARTED.md).

### Changed

- **Retrieval now has a precision pass: results are reranked, not just fused.** Hybrid search is
  recall-oriented by design — it casts a wide net and fuses two arms that disagree about what
  "similar" means — and the top-K was being taken straight off that fusion. At corpus scale that
  fails in a specific way: a firm's templates repeat the same clause across dozens of documents, so
  a window of eight results becomes eight copies of one clause and the model sees one fact repeated
  instead of eight facts.

  `IRagReranker` receives the candidates and returns the final ordering. Retrieval asks it how deep
  a shortlist it wants and widens both arms to match, so `TopK=8` now considers 40 candidates by
  default. A reranker may only re-order and truncate, and it runs **after** every access check, so a
  deeper pool can never surface something the agent scope, collection gate, chunk ACL or metadata
  filter excluded.

  `Rag:Reranker` defaults to **`Mmr`** — maximal marginal relevance over the candidates' own
  vectors. Keyless, deterministic, and costs only arithmetic; at `MmrLambda=0.7` diversity breaks
  near ties without ever letting a merely *different* passage outrank a much more relevant one.
  `Llm` is opt-in and scores each passage against the query with the tenant's chat model
  (cross-encoder) — the most accurate option, at a model call per search; it falls back to
  retrieval order on any failure rather than failing the search. `None` restores the previous
  behaviour.

  **This changes result ordering on upgrade**, because `Mmr` is on by default. Set
  `Rag:Reranker=None` to keep the old ordering exactly.

- **Retrieved passages cite the page they came from.** An answer can now say "p. 7" (or "pp. 3–4"
  when a passage straddles a break) instead of only naming a file — the difference between a
  citation a reader can check and one they have to hunt through. PdfPig already walked the document
  page by page and the number was being discarded; `IDocumentReader.ExtractAsync` now returns the
  text together with each page's character range, `TextChunker` returns chunks as contiguous slices
  with offsets into that text, and the two are joined at ingest into `RagChunk.PageFrom`/`PageTo`.

  Chunks being *slices* rather than paragraphs re-joined with `\n\n` is what makes the offsets
  trustworthy — a chunk stitched from non-adjacent pieces could not honestly claim a page range —
  and it preserves the source text verbatim as a side effect.

  Sources with no pages, and OCR engines that cannot report them, stay null and cite the file alone:
  a default of page 1 would be a fabricated citation, which is worse than none because it looks
  checkable. `IOcrEngine.ExtractAsync` is default-implemented so existing engines are unaffected;
  the Azure Document Intelligence engine overrides it using the page spans its API already returns,
  so scanned documents get page citations too.

- **Knowledge retrieval works outside English, filters by domain facets, and trims per user.**
  The RAG pipeline shipped with a `tsv` column generated as `to_tsvector('english', …)` — a
  generated column cannot vary per row, so every corpus in every deployment was stemmed as English.
  A Spanish or German corpus lost recall on every keyword query. `tsv` is now written at ingest with
  each chunk's own text-search configuration, detected per document (script first, then weighted
  stop-word voting, declining to guess on thin evidence), and the lexical arm builds one constant
  `plainto_tsquery` per configuration in scope so the GIN index still applies. Existing rows are
  stamped `english` by the migration — what they were actually built with — rather than being
  relabelled by the new `simple` default.

  Retrieval now also narrows three ways instead of one, each failing closed and each able only to
  narrow: an agent's `CollectionScopes` (globs over `{module}/{resourceType|-}/{name}`, applied
  server-side so the model cannot escape them by choosing arguments), the existing per-resource
  collection gate, and new per-chunk `Principals` for confidential material inside a shared corpus.
  Free-form `metadata` on collections and chunks is filterable with jsonb containment inside both
  arms — the platform never interprets the keys, which is what lets one design serve legal
  (`jurisdiction=ES`), property, and finance without change.

  Supporting work: `/api/knowledge` and an Admin → Knowledge page (create, configure, index,
  re-index, delete, plus a retrieval preview that runs the agent's exact code path) so building a
  corpus no longer means writing C#; a new `list_knowledge_collections` tool so an agent can
  discover its own corpora and their filter keys; batched embedding writes via `unnest` replacing
  one round trip per chunk; automatic HNSW promotion past `Rag:IndexThresholdChunks` with
  `hnsw.iterative_scan` set database-wide; `ConnectorSyncFile` carrying source principals and
  metadata through Lane B into chunks; and a `FORCE ROW LEVEL SECURITY` backstop on both retrieval
  tables — effective only on a non-superuser connection, which
  [docs/CONFIGURATION.md](docs/CONFIGURATION.md) now states explicitly and the integration test
  proves by dropping privileges. See [docs/PLATFORM_CONNECTORS_RAG_PLAN.md](docs/PLATFORM_CONNECTORS_RAG_PLAN.md) Part 4.

  Breaking for module authors: `IConnectorSyncHandler.OnFilesSyncedAsync` now receives
  `IReadOnlyList<SyncedFile>` instead of `IReadOnlyList<Guid>`, and `IRagService`'s
  `GetOrCreateCollectionAsync`/`IngestFileAsync` gained optional parameters before the
  `CancellationToken` — pass it by name.

- **Role baselines are now declaration-anchored: a tenant stores what it CHANGED, not the whole set.**
  A permission — or a whole role — that a product declares with `AddPlenipoRole` now reaches every
  tenant immediately, including tenants provisioned long before the declaration changed, with no
  reconciler and no per-tenant repair. A permission a tenant admin removed is stored as an explicit
  suppression and survives every later baseline change, and `AddPlenipoRole(..., replace: true)`
  narrowing propagates again. What a role grants is `(baseline ∖ suppressed) ∪ granted`; see
  [ADR 0002](docs/adr/0002-role-permission-deviation-storage.md).

  Previously the first seed materialized every role's full permission set and any row at all made the
  tenant authoritative, so a role with no rows granted nothing. A product that shipped a permission fix
  in a later release therefore shipped something inert on exactly the deployments that had the problem —
  and a role declared after a tenant was seeded conferred no authority at all.

  *Breaking for direct callers:* `RolePermissionResolution.PermissionsForRoles` now takes granted,
  suppressed and baseline maps, and `DatabaseInitializer.EnsureRolePermissionsSeededAsync` is removed —
  nothing replaces it, because role rows no longer need seeding. No compatibility overload is offered:
  one that ignored suppressions would be a privilege-escalation footgun.

- **Upgrade behaviour, stated plainly.** The first start after upgrading converts each existing tenant's
  role rows to deviations **losslessly — nobody's effective permissions change.** A permission a product
  declared *after* a tenant was seeded is therefore recorded as suppressed on that tenant, exactly as it
  behaves today; `DELETE /api/admin/roles/{role}/suppressions` (new, `platform.roles.manage`) restores a
  role to its declared baseline in one call. The conversion claims each tenant with an atomic conditional
  update inside its own transaction, so concurrent instances converge on exactly one conversion — but
  **deploy this release single-instance or with brief downtime anyway**, because the PREVIOUS binary
  misreads converted data. Rolling back past it requires a restore: a converted tenant's grant rows are a
  subset of what the previous resolver expects.

- **`TabEditorField.Options` now carries a label as well as a value** (`TabEditorOption`).
  Canonical identifiers are rarely readable — `America/Mexico_City` is exactly right to store and
  exactly wrong to show — so a field can now say what a human should read while still posting the
  identifier its endpoint expects.

  *Breaking, but only where options come from a variable:* a bare string converts implicitly, so
  `Options: ["checking", "savings"]` is unchanged; an existing array spreads with
  `Options: [.. codes]`.

### Added

- **The web shell can obtain a real bearer token.** Server-side auth was complete, but the shipped web
  client had no way to get a token — no sign-in route, no authority redirect, no callback handler, no
  token store, no refresh, no 401 recovery. `PlenipoClientConfig.authHeaders` was the right seam, but
  overriding it presumed the host already *had* a token, and `@plenipo/ui` did not even export
  `configureClient`, so a product could not reach it without forking the renderer. A correctly
  configured `Auth:Authority` deployment was therefore unreachable from a browser.

  `@plenipo/ui` now exports an `AuthAdapter` seam matching `@plenipo/mobile`'s, ships a
  dependency-free browser PKCE adapter (`createOidcAuth`), and learns its authority at runtime from the
  new anonymous `GET /api/platform/auth-config` — so one prebuilt bundle still serves every deployment.
  Set `Auth:ClientId` (and optionally `Auth:Scopes`) and register `/signin-callback` and
  `/admin/signin-callback` with the IdP. `Auth:ClientId` is deliberately **not** part of the startup
  fail-fast, so an existing API-only deployment keeps starting untouched.

  **In OIDC mode the shell sends no `X-Dev-*` header at all**, including on the SignalR connection —
  a signed-out browser gets a clean 401 and a Sign in button rather than a request that quietly claims
  `X-Dev-Roles: system_admin`. With no authority configured the dev headers still apply, so a local host
  works with nothing configured.

- **A `Bootstrap` configuration section creates the deployment's first tenant and its operator.**
  Outside Development the platform seeded nothing, and because a request's permissions are resolved only
  *after* its tenant resolves, every principal on a tenant-less deployment carried an empty permission
  set — including one asserting `system_admin`. `POST /api/admin/tenants`, the endpoint that would have
  fixed it, was therefore unreachable. That is a deadlock, not a permissions problem.

  The section is consumed once at startup, is never exposed over HTTP, and no-ops the moment any
  principal in the deployment holds an operator-reserved permission — deliberately not merely when a
  tenant exists, since a commerce-provisioned tenant gets a `tenant_admin`, who cannot create tenants.
  Every run is audited as `PlatformBootstrapped`. `Bootstrap:AdminSubject` is **required** when
  `Bootstrap:AdminRoles` grants an operator-reserved permission: without it the roles bind through an
  email-keyed invite, and email comes from an unverified token claim. The shipped Docker Compose
  deployment — which defaults to `Production` — now carries the variables and tells you to remove them
  after the first start.

### Fixed

- **Dev-auth now resolves the caller on hub paths, so chat over SignalR is no longer always
  `system_admin`.** A browser's WebSocket handshake cannot set request headers, so SignalR can only
  carry the dev identity in the query string. `DevAuthenticationHandler` read `Request.Headers` only,
  and every `/hubs` turn in Development therefore fell through to its defaults — subject `dev-user`,
  tenant `dev`, roles `system_admin` ⇒ `["*"]`. The pre-model-call tool filter offered tools RBAC
  should have removed and approvals parked in a tenant nobody addressed, which meant RBAC-shaped
  behaviour "verified" over the hub proved nothing about RBAC.

  The handler now reads `X-Dev-*` from the query string for **hub paths only** — the same restriction,
  for the same reason, that `AuthSetup` already puts on the JwtBearer `access_token` parameter: a query
  string reaches browser history and proxy logs, and the REST surface can carry headers perfectly well,
  so it keeps ignoring identity in the URL. A header still wins where both are present, and the
  absent-vs-present-but-empty `X-Dev-Roles` asymmetry is preserved.

  Development-only: the handler is registered only when no real authority is configured. The shipped
  `@plenipo/ui` still sends no identity in the hub URL and is unchanged — its dev identity is a
  constant equal to these same defaults, so emitting it would be a no-op. This is what lets a product's
  dev identity switcher or e2e harness drive a real identity over the hub.

- **A request whose tenant does not resolve now says so.** `GET /api/platform/me` reports
  `tenantResolved: false` with a `tenantProblem` naming the cause, the resulting 403 carries the same
  explanation in its body, and the SignalR hub raises it as a `HubException`. Previously `/me` returned a
  cheerful 200 while everything else returned a bare 403, so a client shell rendered normally and then
  failed every call with nothing anywhere naming the cause — which, on a fresh deployment, is the entire
  symptom of having no tenant at all. No status code and no authorization decision changed.

- **The `Auth:RequireMfa` backstop can no longer be deleted by accident.** The `JwtBearerEvents` bag was
  constructed *inside* the `RequireMfa` branch, so the next handler to need an event would have replaced
  it and silently removed the MFA enforcement `SECURITY.md` advertises. It is now constructed
  unconditionally and events are attached to it.

- **The SignalR hub URL no longer carries dev-auth values as query parameters.** A comment claimed "the
  server reads either"; nothing in the platform reads `Request.Query` for identity, so they authenticated
  nothing — while putting `X-Dev-Roles: system_admin` into browser history, proxy logs and error reports.
  Credentials now come from the configured client, with the bearer travelling via `accessTokenFactory`;
  the host reads an `access_token` query parameter back for `/hubs` paths only, which is the one
  transport a browser cannot give a header to.

- **A tenant with a pending approval and no eligible approver now logs a warning.** It was a debug
  line, so the state was effectively invisible: every approval-gated write parks until an operator
  intervenes, and the only symptom is work silently not happening. Deployments on
  `Auth:PermissionSource=Token` keep the debug line, where having no DB-enumerable approver is expected.

- **`GET /api/platform/info` and the ops AI card now report the TENANT's provider, not the
  deployment's.** Both were written when AI configuration was deployment-only; runtime per-tenant
  provider switching landed days later and neither surface caught up. Consequences a product hit in
  the field: a tenant that configured a real provider kept being shown the "Demo mode" banner, a
  tenant that set `Provider = "None"` still got a Chat tab and only learned otherwise mid-turn, and
  `/api/admin/ops` showed the deployment's provider/model beside the tenant's real token spend — one
  unlabelled card contradicting itself at a glance. `/api/admin/ai-settings` is the surface that
  legitimately shows both, and it keeps them in separate named fields rather than merging them.

  `/info` needs authentication but no permission, so it stays reachable without a resolved tenant; in
  that case it answers from the deployment defaults explicitly rather than depending on how a null
  tenant filter translates.

- **The OpenAI model catalog is narrowed to chat-capable models.** `POST /api/admin/ai-models`
  returned OpenAI's whole account-wide catalog — image, TTS, transcription, embeddings, moderation,
  legacy completions, well over a hundred ids — so the admin's model picker offered `dall-e-3` for a
  chat assistant. Filtering happens in the OpenAI arm only (Anthropic publishes nothing but chat
  models; Ollama's ids are arbitrary local names that a chat-family gate would reject wholesale) and
  **before** the 1000-id cap, so a large catalog no longer loses chat models past the alphabetical cut.

  It is not silent: `AiModelCatalogResult.Message` reports how many were hidden and points at the
  type-an-id-by-hand escape hatch. OpenAI's `/v1/models` exposes no capability field, so name
  patterns are the only signal available and will need occasional amendment — see
  `OpenAiChatModelFilter`. Pinned dated snapshots (`gpt-4.1-2025-04-14`) are deliberately KEPT:
  suppressing them would be a reproducibility policy dressed up as a capability fact.

- **AI Settings no longer offers a "Default" model for a provider that cannot inherit one.** The
  Model dropdown promised `Default: <deployment model>` for every provider and let you save it; the
  server then rejected the entire save. The server is right — a model is part of a *connection*, like
  the endpoint and the key, and a deployment may not default to OpenAI or Anthropic at all, so the
  deployment's model id belongs to a different provider. The form now mirrors that rule, disables
  Save, and says why, instead of sending a request guaranteed to 400.

### Added

- **A mobile app — `@plenipo/mobile`, the same manifest rendered natively.** A React Native / Expo
  shell that builds its tab bar, lists, editor forms, singleton forms, charts, detail documents,
  tab and row actions, and chat from `GET /api/platform/modules`. Installing a module in a C# host
  puts it on phones that already have the build: shipping domain capability becomes a backend
  deploy rather than an App Store review. A product's app is a base URL and a brand
  (`frontend/mobile-app` is the template), with the same `defineModule` registry the web shell uses
  for the tabs that need a native screen. Chat rides the AG-UI SSE endpoint rather than the SignalR
  hub — a WebSocket doesn't survive a phone's backgrounding — but both drive the same
  `AuthorizedAgentRunner`, so RBAC filtering, approvals, audit and token accounting are identical.
  See [docs/MOBILE.md](docs/MOBILE.md).

- **`@plenipo/client`** — the renderer-free contract extracted out of `@plenipo/ui`: the TypeScript
  mirror of every C# descriptor, the REST surface, the AG-UI transport, the `PermissionMatcher`
  mirror, form defaults, chart shaping, and row-template resolution. No React, no DOM, no bundler
  globals, enforced by a test that reads the sources. This is what keeps two renderers from drifting
  apart on the manifest. `@plenipo/ui`'s public API is unchanged — it re-exports the same names.

- **Mobile push, through the existing notification seam.** A `UserDevice` entity plus
  `PUT/GET/DELETE /api/notifications/devices` (self-scoped; a push token is never echoed back), and
  a `PushNotificationChannel` fanning out to a pluggable `IPushTransport`. The built-in Expo
  transport fronts APNs and FCM with no Apple or Google credentials in the repo or CI. Registration
  is idempotent per installation, because tokens rotate; tokens the service reports as gone are
  deleted. `Push:IncludeContent=false` withholds the title and body from the push service for
  deployments handling privileged material. The channel is inert until a device registers, so a
  deployment with no mobile app configures nothing.
- **Detail-document sections carry a `tone`** — a section may say what it *means* (`"info"`,
  `"success"`, `"warning"`, `"danger"`) and the shell renders it as a callout: bordered, tinted, and
  carrying the severity as a word in the accessibility tree, never as colour alone. Before this, a
  hard extraction failure and a table of names rendered in exactly the same grey, so the only way a
  module could raise its voice was to shout in the heading text.

  *Additive:* a section without a tone renders precisely as it did. The detail document is untyped
  server-side, so an unrecognized tone deliberately degrades to an untoned section rather than
  breaking the page — nothing can catch the typo for you.

- **`TabColumn.LinkTemplate`** — a column may make its value navigable, pointing at a **client**
  route (never an API path), optionally with `{field}` placeholders resolved from the row the way a
  row action's endpoint template is. Unlike a row action the placeholder is optional: a fixed route
  landing every row on the same tab is the common, useful case. `Masked` wins if a column declares
  both — hiding a value and inviting a click on it are contradictory instructions.

  *Additive, and deliberately not a positional parameter:* `LinkTemplate` is an init-only property,
  so `TabColumn`'s primary constructor and generated `Deconstruct` stay binary-compatible for a
  product already compiled against a published package. A column declaring no link renders plain
  text exactly as before.

  Two renderers that previously stringified cells directly — the sub-table inside a detail section
  and the read-only singleton form — now go through the shared cell renderer, so `Masked` and
  `LinkTemplate` mean the same thing everywhere a `TabColumn` is declared. **`Masked` was silently
  ignored inside a detail section before this**; if a module declared it there expecting plain text,
  the value now renders masked.

- **`TabEditorField.Default` / `.DefaultFrom`** — a field may say what it should start as.
  `Default` is a constant the manifest knows; `DefaultFrom` (see `FieldDefaultSources`) is for what
  only the viewer's browser can answer — today `browser-timezone` and `browser-currency`, so a setup
  wizard fills in where the user actually lives (and their likely currency) instead of asking them
  to hunt for it, or silently defaulting to UTC/USD. `browser-currency` is an explicit *guess* from
  the browser locale's region — always editable, and validated against the field's options like any
  default.

  The shell still never pre-picks an option on its own; a default is a field *declaring* its
  starting point, always editable, and never posted behind the user's back. A default the field's
  own vocabulary doesn't contain is ignored rather than offered and then rejected by the endpoint.
  Defaults apply to a blank form only — editing a record shows the record.

- **Singleton config tabs render as a form, not a table** — `TabDescriptor.Singleton` marks a
  `DataEndpoint` that is one config object rather than a list. The shell renders its single row as a
  labeled form (the editor's fields, prefilled from the row, saved as a whole) instead of a table
  with an Add button that never made sense for a single row. `TabEditorField.Group` sections the
  form under headings. Callers without the editor's permission see the values read-only. The
  endpoint contract is unchanged — it still returns a one-element array.

- **Detail documents can carry actions** (`TabDetailDocument.actions`) — the drill-down a
  `DetailEndpoint` returns may now include commands on the record it describes (close a matter,
  mark a shipment delivered, approve an import batch), and the generic detail view renders them
  beside the content the user is looking at. Each action POSTs to its endpoint, gets a confirm dialog when it declares
  `confirm`, and may declare one input `field` (a `TabEditorField`, so a select can draw live
  options from an endpoint — the button stays disabled until a value is chosen). The response
  message renders as a visible banner, error-styled on a non-2xx answer, and the banner outlives
  the refetch — approving is exactly the action whose success leaves nothing else to do, and
  "Posted 12 transaction(s)…" must not vanish with the buttons. The document and the tab data
  behind it refresh after every run, and the Back button now renders in loading/error states too,
  so an action that removes the record (discard) can't strand the viewer. The server composes the
  list per caller and per record state; the field is additive, so existing detail payloads render
  unchanged.

**Platform (backend NuGet packages)**
- **Module SDK** — a vertical implements `IModule` and declares a `ModuleManifest` (tools, tabs,
  roles, agent instructions); the host discovers and installs it with `AddPlenipoModule<T>()`.
  See [BUILDING_A_MODULE.md](BUILDING_A_MODULE.md).
- **Chat-first agent pipeline** on Microsoft Agent Framework over `Microsoft.Extensions.AI`, streamed
  over SignalR (Redis backplane) and the open **AG-UI** protocol.
- **Tool security before the model call** — the agent runner filters tools by the caller's permissions
  before building the request, so the LLM never sees a tool the user may not call.
- **Human-in-the-loop approvals** — side-effecting tools are blocked pending explicit approval, and
  each decision (approved + result, failed, or rejected) is reported back into the originating
  conversation's next turn — the assistant answers from the outcome instead of forever repeating
  "still pending" (the approval happens outside the chat, so without this hand-back the model
  never learns it was decided). Resolving is never silent for the humans either: the acting button
  shows the execution in progress, the outcome lands in the transcript the moment the click
  resolves (persisted server-side, so reloads agree), and the requester gets an inbox ping when
  someone else decided.
- **Layered RBAC** (system roles → dotted permissions with wildcards → per-resource ACLs), an
  append-only **audit log**, and per-turn **token-usage** tracking.
- **Multi-tenant by default** — row-level isolation via EF Core global query filters on `TenantId`.
- **External-IdP authorization mode** — `Auth:PermissionSource=Token` makes Entra External ID / B2C
  the single source of truth: roles come exclusively from the token, internal role assignments and
  per-user grants are ignored (their admin endpoints answer 409 with guidance), and JIT provisioning
  never invents a default role. Role → permission baselines remain the translation layer from IdP
  role names to fine-grained tool permissions.
- **Provider-swappable AI** (OpenAI / Azure OpenAI / Anthropic / Ollama) plus a dependency-free **Mock**
  provider so chat — and real, audited tool calls plus the approval gate — work with zero configuration.
  Tenants switch provider/model at runtime in AI Settings (BYO key, vaulted write-only); the valid
  provider list is single-sourced in `AiProviders`.
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
- **Recurring jobs** — a module declares scheduled work manifest-first
  (`ModuleManifest.RecurringJobs`: kind + Hourly/Daily/Weekly cadence + description); the platform
  enqueues it once per cadence window for every tenant with the module enabled, executed by the
  module's registered `IJobHandler` under a tenant-scoped **system identity** (no user; the
  module's tool wildcard as authority; audit attributes the run to the scheduler). A per-tenant
  last-run cursor makes restarts **catch-up-one**: a missed window fires once on the next sweep,
  never once per missed window, and never double-fires.
- **Approval notifications** — when a side-effecting tool call lands in the approval queue, every
  tenant user whose DB-sourced authority grants `chat.approvals.manage` gets one in-app
  notification (category `"{moduleId}.approvals"`, mutable per user via the standard switchboard),
  so approvers act from their inbox instead of camping in the requester's chat.
- **Separate-systems model** — each vertical is its own product/host/repo on the platform packages
  (`samples/Plenipo.Legal.Host` is the canonical single-vertical shape); systems connect via the
  **plenipo-peer connector**, which lets one deployment's agent ask another's over the open AG-UI
  protocol (the peer enforces its own auth, RBAC, tool gating, and audit; credential is a protected
  secret).
- **Data-source connectors** — a manifest-first **connector SDK** (`Plenipo.Connectors.Sdk`:
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
- **`plenipo` CLI** — `plenipo init` (interactive wizard or `--non-interactive` flags) writes one
  declarative `plenipo.settings.json` the host layers into configuration; re-runs are
  non-destructive and secrets are never written (user-secrets commands are printed instead).
- **Permission-aware RAG** (opt-in, `Rag:Enabled`) — documents ingest into **scoped collections**
  (per matter/project, the Harvey-Vault pattern) via a background job; retrieval is **hybrid**
  (pgvector + tsvector fused with RRF, tenant/collection predicates in both arms) through the
  `search_knowledge` platform tool, with per-passage file citations. Access to a resource-bound
  collection goes through the owning module's `IRagCollectionGate` and **fails closed**. Embeddings
  ride the swappable `IEmbeddingGenerator` seam — a deterministic **Mock embedder** keeps the whole
  pipeline keyless in dev/CI. Requires pgvector (dev/CI images updated). See
  [docs/PLATFORM_CONNECTORS_RAG_PLAN.md](docs/PLATFORM_CONNECTORS_RAG_PLAN.md).

**Frontend (`@plenipo/ui`, `@plenipo/admin-ui`)**
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
- **Editable server-driven tables** — a tab may declare a `TabEditor` (upsert/delete endpoints,
  fields with `Numeric`/`Multiline`/`Required`, a `KeyField` for edit identity) and the shell grows
  Add/Edit/Delete with zero module UI. Numeric fields post JSON numbers; blank optional fields are
  omitted, not sent as `""`. Affordances ship only to callers holding the editor's permission.
- **Row drill-down** — a tab's `DetailEndpoint` template gives every row a View button rendering a
  generic detail document (prose + table sections). See [BUILDING_A_MODULE.md](BUILDING_A_MODULE.md).
- **Connected accounts page** — end users link/unlink their own delegated-connector accounts at
  `/account/connections` (backed by `GET /api/connectors` + `DELETE /api/connectors/{id}/login`),
  reachable from the top-bar user name; the admin console is no longer the only door.
- **Notification delivery card** — the admin Operations page edits the webhook URL + signing secret
  (write-only: `null` keeps, `""` clears) where its health was already reported.
- **Upload preflight** — `/api/platform/info` publishes `maxUploadBytes` and the composer refuses an
  oversized attachment before uploading, with the server's 413 as the backstop.

- **Google Drive connector** — the second delegated (per-user OAuth) data source; the OAuth
  machinery is IdP-agnostic now (manifest URL templates: Entra default, fixed-URL IdPs like
  Google supported, Authority optional).
- **Email delivery** — `ISmtpTransport` seam + `EmailNotificationChannel` in the notification
  fan-out (`Email:` config; password via user-secrets/Key Vault).
- **Host extensibility** — `ITenantProvisionedHook` (act on provisioning; welcome-email worked
  example), `AddPlenipoNotificationChannel` / `AddPlenipoPlatformTools` first-class helpers,
  `Auth:DefaultRole` for JIT users, and [BUILDING_A_PRODUCT.md](BUILDING_A_PRODUCT.md)
  cataloging every seam.

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
