# Platform plan: connectors, install wizard, and permission-aware RAG

Three platform capabilities, designed together because they share seams:

1. **Connectors** — pluggable bridges to where a customer's data already lives
   (SharePoint / Microsoft 365, Azure Blob, iManage, Google Drive, …), enable/disable-able
   per tenant, easy for domain modules to declare and consume.
2. **`plenipo init` CLI wizard** — an OpenClaw-style installer that asks which channels,
   connectors, and features to enable and writes one config.
3. **RAG pipeline** — opt-in document ingestion into permission-aware, *scoped* retrieval
   collections ("smaller RAG databases per case"), queryable from chat.

Grounded in two research passes (2026-07): enterprise-AI connector models
(Harvey, Legora, CoCounsel, Microsoft 365 Copilot connectors, Glean, OpenClaw's wizard) and
permission-aware RAG state of practice (Azure AI Search security trimming, pgvector 0.8
multi-tenancy, Harvey Vault / Intapp ethical walls, hybrid retrieval). Key citations inline.

---

## Part 0 — What the industry converged on (research digest)

**Connectors: a two-lane taxonomy is now industry-standard.** Microsoft 365 Copilot
formalizes it ([overview](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/overview));
Harvey ([Connector Library](https://www.harvey.ai/blog/connector-library)) and CoCounsel
(DMS fetch vs Knowledge Search) mirror it:

| | Lane A — **federated / on-demand** | Lane B — **synced / indexed** |
|---|---|---|
| Fetch | live, at query time | crawled/ingested ahead of time |
| Auth | **each user's own token** (OAuth on first use) | service credential; **ACLs ingested with content** |
| ACL enforcement | the source enforces, per fetch | platform trims results at query time ("permission trimming", Glean) |
| Staleness | none | sync lag — treat ACL copies as a cache |
| Examples | Harvey iManage picker, MS federated (MCP), CoCounsel DMS | MS Graph connectors, Glean, CoCounsel Knowledge Search |

**Admin enablement is consistently two-stage**: a tenant admin registers/enables the
integration (app registration in the source + a toggle on an Integrations page), then each
user authenticates individually on first use. **Disable = revoke tokens** (Harvey states
this explicitly; re-enable forces re-auth).

**Scoped binding**: Harvey binds at most **one** synced folder/matter per Vault project —
per-project connector bindings instead of global indexing. This matches matter-centric
products and is the pattern Plenipo should copy for module resources.

**RAG in legal AI converged on scope-first retrieval**: Harvey Vault makes each project its
own corpus (~1,000 files) with explicit sharing; retrieval never spans projects the user
can't see. Ethical walls arrive by **policy sync from an external system of record**
(Intapp Walls → Harvey, Feb 2026), enforced at retrieval/context/output layers,
**failing closed** with per-document audit. That is exactly the user's instinct of
"smaller RAG databases for smaller cases".

**Retrieval mechanics (2025–26 defaults)**:
- Hybrid **BM25/full-text + vector, fused with RRF** — ~⅓ of real queries contain exact
  identifiers embeddings miss. Tenant/ACL predicates must be applied **inside both arms**,
  never after fusion.
- pgvector's filtered-HNSW recall problem is real; pgvector **0.8.0 iterative scans**
  (`hnsw.iterative_scan = relaxed_order`) mitigate it, but the cleaner answer for us is
  **collections, not one big index** — small collections (<~10–50K chunks) use exact scan
  (perfect recall, zero index maintenance).
- **Per-chunk provenance is table stakes**: `document_id` + page/span on every chunk so
  answers cite.
- Azure's canonical "**security filter**" pattern: principal IDs denormalized onto rows,
  filtered at query time, with the caveat that it's a cache of source ACLs
  ([docs](https://learn.microsoft.com/en-us/azure/search/search-security-trimming-for-azure-search)).
- .NET seam: **`IEmbeddingGenerator<string, Embedding<float>>`** (Microsoft.Extensions.AI, GA).
  Vectors from different models are not comparable — store `embedding_model` per chunk row.

**OpenClaw's installer UX** (the template the user asked for): timeline of steps shown
upfront, skippable optional steps, **QuickStart vs Advanced** modes, channels/skills as
multi-selects with plugin-install-on-select, one JSON config as source of truth,
**idempotent re-runs** (nothing wiped without `--reset`), credentials stored *outside* the
config, and a full `--non-interactive` flag surface for scripting.

---

## Part 1 — Connectors

### 1.1 Design principles

- **Mirror the module pattern.** Connectors are to *data sources* what `IModule` is to
  *domains*. Same lifecycle: a manifest, DI registration, per-tenant enablement, dotted
  permissions, audited tool calls. A team that has written a Plenipo module can write a
  connector without learning anything new.
- **Both lanes, Lane A first.** On-demand connectors (browse/search/fetch as the current
  user) are smaller, have zero ACL-staleness risk, and unblock the lawyer scenario
  ("attach the contract from our SharePoint to this matter"). Lane B (sync → RAG ingestion)
  builds on Part 3 and reuses the background-job runner.
- **Fetched files land in the platform file store.** A connector fetch materializes a
  `StoredFile` (tenant-scoped, audited, source = connector id) — everything downstream
  (attachments convention, `read_document`, matters, RAG ingestion) already works on
  `StoredFile`. Connectors need no special downstream integration.

### 1.2 Contract (new project: `Plenipo.Connectors.Sdk`, mirroring `Plenipo.Modules.Sdk`)

```csharp
public interface IConnector
{
    ConnectorManifest Manifest { get; }
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
}

public sealed record ConnectorManifest
{
    required string Id { get; init; }            // "sharepoint", "azure-blob", "msgraph"
    required string Name { get; init; }
    required string Description { get; init; }
    ConnectorAuthMode AuthMode { get; init; }    // UserDelegated (Lane A) | Service (Lane B) | Both
    bool SupportsSync { get; init; }             // can participate in RAG ingestion (Lane B)
    IReadOnlyList<ConnectorSettingDescriptor> Settings { get; init; }  // schema-driven admin config
                                                 // (tenant id, site URL, …) — secrets marked IsSecret,
                                                 // stored via the existing secret conventions, never in config JSON
}

/// <summary>Tools the connector contributes to agents — browse/search/fetch, RequiresApproval-able.</summary>
public interface IConnectorToolSource
{
    string ConnectorId { get; }
    IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices);
}
```

Tool permissions follow the existing dotted scheme: `connectors.sharepoint.search`,
`connectors.sharepoint.fetch` — so role baselines, the admin security catalog, wildcards,
and the pre-model-call tool filter all apply with **zero new authorization code**.
A connector's typical tool set: `search_<source>`, `list_<source>_folder`,
`fetch_from_<source>` (fetch = materialize into `IFileStore`, gated `RequiresApproval`
by default since it copies data into the platform).

**MCP is a connector implementation detail, not a separate concept.** A future
`McpConnector` can wrap a remote MCP server's tools into an `IConnectorToolSource`
(MAF/MEAI already speak MCP client) — that's how Harvey and Microsoft scale their catalogs.
The contract above doesn't change.

### 1.3 Per-tenant enablement — two-stage, like everyone else

New entities (platform DB, tenant-scoped by the existing global query filters):

```
TenantConnector       (TenantId, ConnectorId, Enabled, SettingsJson, EnabledBy, EnabledAt)
UserConnectorLogin    (TenantId, UserId, ConnectorId, EncryptedTokens, ExpiresAt, ConnectedAt)
ConnectorBinding      (TenantId, ConnectorId, ModuleId, ResourceType, ResourceId, ExternalRef, SyncMode)
```

- **Stage 1 (tenant admin)**: enable + configure the connector on a new admin console
  **Integrations** page (`/api/admin/connectors`), schema-driven from
  `ConnectorManifest.Settings` — the exact pattern the RBAC role editor already uses.
  Unlike modules (default-on), connectors are **default-off**: they reach outside the
  platform boundary, so enabling is an explicit, audited admin act.
- **Stage 2 (each user)**: first use of a delegated connector tool returns a "connect your
  account" prompt with an OAuth link (surfaced in chat the same way HITL approvals are);
  tokens encrypted at rest in `UserConnectorLogin`.
- **Disable revokes**: disabling a `TenantConnector` deletes/invalidates all its
  `UserConnectorLogin` rows and the resolver stops offering its tools — same
  "hidden *and* uninvocable" guarantee `ITenantModuleStore` gives modules today.
- A `ITenantConnectorStore` mirrors `ITenantModuleStore` and is consulted by the agent
  runner when assembling tools.

### 1.4 How domain modules consume connectors

- `ModuleManifest` gains `IReadOnlyList<string> RecommendedConnectors` — purely
  informational: the installer wizard and admin UI use it to suggest ("the Legal module
  works best with SharePoint/iManage"). No hard dependency; modules must degrade gracefully.
- **Scoped binding (the Harvey Vault pattern)** via `ConnectorBinding`: e.g. bind matter
  *Vendor diligence* to SharePoint folder `/sites/legal/vendor-x`. Lane A meaning: agent
  tools scope search/fetch to the binding. Lane B meaning (later): the sync job ingests
  that folder into the matter's RAG collection. One folder per resource, like Harvey.

### 1.5 First connectors to ship

1. **`azure-blob`** (service-auth, Lane A read) — trivial given `AzureBlobFileStorage`
   exists; validates manifest/enablement/tools end-to-end. Plus a **`local-folder`**
   dev/test connector so integration tests stay keyless.
2. **`msgraph` (SharePoint/OneDrive)** — the one the user named; delegated OAuth,
   `Microsoft.Graph` SDK (MIT). This proves the Stage-2 user-consent flow.
3. Later, by demand: Google Drive, iManage/NetDocuments (partner-program gated), IMAP/Gmail.

---

## Part 2 — `plenipo init`: the install wizard

A .NET global tool (`dotnet tool install -g Plenipo.Cli`), new project `src/Plenipo.Cli`,
using **Spectre.Console** (MIT) for the wizard UX. Copies OpenClaw's proven shape:

**Timeline shown upfront; QuickStart vs Advanced; every step skippable and revisitable:**

```
plenipo init
  1. Detect existing install        → keep / review / reset (never wipes without --reset)
  2. Prerequisites check            → Docker, .NET 10, Node 20 (fail with fix instructions)
  3. Deployment mode                → local dev (Aspire) | server (compose/Terraform pointers)
  4. AI provider                    → Mock (default — works with zero keys) | OpenAI | AzureOpenAI | Ollama
  5. Channels     [multi-select]    → Web UI (always on) | WhatsApp | Telegram (future)
  6. Connectors   [multi-select]    → from the ConnectorManifest catalog; per-selection
                                      settings prompts, driven by ConnectorSettingDescriptor
  7. Knowledge / RAG                → enable ingestion pipeline? embedding provider?
  8. Modules                        → which installed modules to enable for the first tenant
  9. Admin bootstrap                → first tenant slug + admin user
 10. Health check                   → boots the stack, hits /alive, one Mock chat turn
```

Rules carried over from OpenClaw, adapted to .NET conventions:

- **One declarative config as source of truth** — the wizard *writes standard ASP.NET
  configuration* (`plenipo.settings.json` layered onto appsettings), because every knob it
  touches (Ai:\*, Files:\*, Channels:WhatsApp:\*, Connectors:\*, Rag:\*) is already bound
  via `IConfiguration`. No parallel config system.
- **Secrets never in the file**: wizard pipes them to `dotnet user-secrets` (dev) or emits
  Key-Vault/env-var references (server mode), matching the repo's existing rule.
- **Idempotent re-runs**: re-running `plenipo init` diffs and updates; `--reset` to start over.
- **Full non-interactive surface** for CI/scripts:
  `plenipo init --non-interactive --ai-provider Mock --channels web,whatsapp --connectors azure-blob --enable-rag --json`.
- Post-install single-section changes: `plenipo configure connectors`, `plenipo configure channels`
  (same prompts, section-scoped).

The wizard builds its channel/connector/module catalogs **from the manifests in the
installed packages** — adding a connector to the catalog is just shipping the NuGet package;
the CLI discovers it. (Step 6 can also offer known-but-not-installed connectors and
`dotnet add package` them on select — OpenClaw's plugin-install-on-select — in v2.)

---

## Part 3 — Permission-aware RAG

### 3.1 Shape: collections, not one big index

A **collection** is a scoped corpus — per matter, per project, per tenant knowledge base.
This is Harvey Vault's model, it answers "smaller RAG databases for smaller cases", and it
sidesteps pgvector's filtered-HNSW recall problem: most collections stay small enough for
**exact scan** (perfect recall, zero index maintenance); HNSW (with
`hnsw.iterative_scan = relaxed_order`) only where a collection grows past ~10–50K chunks.
Blast radius (re-embeds, deletes, rebuilds) stays per-collection.

```
RagCollection (Id, TenantId, ModuleId?, ResourceType?, ResourceId?, Name, EmbeddingModel, CreatedBy…)
   e.g. ("legal", "matter", <matterId>)  ← a matter's corpus, created on first ingest
RagChunk      (Id, TenantId, CollectionId, DocumentFileId → StoredFile, Ordinal,
               Text, Tsv tsvector, Embedding vector(n), EmbeddingModel,
               PageFrom?, PageTo?, ContentHash, AclJson)
```

Postgres already runs the platform (Aspire), so retrieval infra is **pgvector + tsvector in
the same database** — no new service, and the tenant global-query-filter + RLS-backstop
story extends naturally. Composite indexes lead with `(TenantId, CollectionId)`.

### 3.2 Authorization: two layers + fail-closed recheck

Mirrors Intapp-wall (coarse) + DMS ACL (fine), on seams Plenipo already has:

1. **Collection gate (coarse)** — querying a collection requires a dotted permission
   (`rag.query` + module scoping) **and**, when the collection is bound to a module
   resource, access to that resource. This is where **legal item 10 (matter ACLs / ethical
   walls) lands**: a per-resource ACL on `Matter` gates its collection; the wall is enforced
   *before* any vector math happens. Scope-first retrieval, like every legal AI product.
2. **Chunk trim (fine)** — `AclJson` (principal/role snapshot, from the source ACL for
   synced content or the owning resource's ACL for uploads) filtered **inside both arms**
   of the hybrid query — the Azure security-filter pattern. Denormalized ACLs are a cache:
   ACL changes enqueue a re-sync **background job** (the capability-capture runner fits
   exactly — the job re-stamps chunks under the enqueuer's authority).
3. **Fail-closed post-check** — after fusion, the final top-k is re-verified against the
   live per-resource ACL; anything unconfirmable is dropped and the drop is audited
   (Harvey/Intapp behavior). Missing ACL metadata ⇒ not retrievable, never default-open.

**RLS as backstop** on `rag_chunks` — hybrid search is the one place we'll write raw SQL,
which bypasses EF's global filters; RLS keeps a bug from becoming a cross-tenant leak.

### 3.3 Retrieval: hybrid by default, cited by construction

One SQL function: pgvector similarity + `tsvector` rank, tenant/collection/ACL predicates
in **both** arms, fused with **RRF** (BM25-ish and cosine scores aren't comparable; RRF
needs no normalization). Every hit returns `DocumentFileId + PageFrom/PageTo` — which slots
straight into the existing citation convention (`file id: <guid>`), so chat answers cite
and the UI can deep-link through `/api/files/{id}`.

Exposed as a platform tool via `IPlatformToolSource` (like `read_document`):
`search_knowledge(query, collection?)`, permission-gated `rag.query`, audited like every
tool call. Modules can prescribe it in `AgentInstructions` ("answer matter questions from
search_knowledge results, cite file ids").

### 3.4 Ingestion: a background job, per collection

`rag.ingest` jobs on the existing `IJobQueue`/`JobProcessor` (progress reporting, capability
capture, cancel API — all already built):

```
StoredFile → IDocumentReader.ExtractTextAsync (PdfPig / OCR seam — already built)
          → structure-aware chunking (~512-token target, page-aligned boundaries)
          → IEmbeddingGenerator<string, Embedding<float>>  ← MEAI seam, DI-swappable
          → RagChunk rows (tsv via trigger; EmbeddingModel stamped per row)
```

- **Keyless testing stays intact**: a deterministic `MockEmbeddingGenerator` (hash-based
  vectors) mirrors the Mock chat provider — ingestion + retrieval integration tests run in
  CI with no API keys, same philosophy as everything else in the repo.
- **Model migration**: `EmbeddingModel` per chunk + re-embed as a per-collection job
  (dual-write, verify, swap) — vectors from different models never get compared.
- **Triggers**: explicit tool (`index_matter_documents`, RequiresApproval), automatic on
  attach (module opt-in), or a Lane-B connector sync job feeding the same pipeline.
- RAG is **opt-in per deployment** (`Rag:Enabled`, wizard step 7) and per module — a
  domain system that doesn't need it pays nothing.

---

## Build order (proposed)

| Phase | Slice | Why first |
|---|---|---|
| **1** ✅ | RAG core: `RagCollection`/`RagChunk` + pgvector, ingestion job, hybrid+RRF query with tenant/collection filters + fail-closed recheck, `search_knowledge` tool, `MockEmbeddingGenerator`, matter-scoped collections in Legal | **Shipped.** `IRagService`/`IRagCollectionGate` (Application seams), raw-SQL hybrid RRF with the embedding-model pin, `platform.rag-ingest` job, `tools.knowledge.search_knowledge` platform tool, legal `index_matter_documents` + matter walls (`restrict_matter_access` / `open_matter_access` — item 10), pgvector images in AppHosts/compose/Testcontainers. Deferred within phase 1 → **all closed in phase 6** except page-range provenance (extraction is still not page-aware) |
| **2** ✅ | Connector SDK + enablement: `Plenipo.Connectors.Sdk`, `TenantConnector`/`ITenantConnectorStore`, admin Integrations page, `azure-blob` + `local-folder` connectors (Lane A) | **Shipped.** `IConnector`/`ConnectorManifest`/`IConnectorToolSource`/`IConnectorSettings` (SDK package), default-OFF per-tenant enablement consulted by the agent runner (`IConnectorToolCatalog` — a disabled connector's tools are never built), `/api/admin/connectors` with schema-driven settings (secrets write-only + DataProtection at rest), admin-ui Integrations page, `AddPlenipoConnector<T>()`, `tools.connectors.{id}.{tool}` permissions in the security catalog, fetch-lands-in-`IFileStore` convention. Deferred within phase 2: per-user OAuth (`UserConnectorLogin`, disable-revokes-tokens) — arrives with `msgraph` in phase 4 |
| **3** ✅ | `plenipo init` wizard (QuickStart + non-interactive), catalogs driven by manifests | **Shipped.** `Plenipo.Cli` dotnet tool (`plenipo init`): stepped wizard (AI/RAG/documents/channels/storage/auth) + full `--non-interactive` flag surface; writes one declarative `plenipo.settings.json` the platform layers between appsettings.json and the environment file; re-runs are non-destructive; secrets never written — the wizard prints the `dotnet user-secrets` commands. Deferred within phase 3: prerequisite checks, health-check boot, and install-on-select (connectors/modules are per-tenant runtime toggles in the admin console, which the wizard points at) |
| **4** ✅ | `msgraph` (SharePoint/OneDrive) delegated connector + per-user OAuth flow + disable-revokes | **Shipped.** Platform-level delegated-auth machinery (`UserConnectorLogin` protected token sessions, auth-code+PKCE start/callback endpoints with data-protected state, transparent refresh, `IOAuthTokenClient` seam) + the `msgraph` connector (Graph v1.0 REST via `IGraphApiClient` seam — no Graph SDK dependency; `list_m365_files` / approval-gated `fetch_from_m365` ride the CURRENT user's token, so Graph enforces their own permissions). **Disable revokes every session**; re-enable forces re-auth. E2E-tested keylessly with a fake IdP + fake Graph while the platform flow stays real |
| **5** ✅ | Lane B: connector sync jobs → RAG ingestion, `ConnectorBinding` scoped folders (Harvey-style), ACL snapshot sync | **Shipped** (ahead of 4 — keyless-testable). `IConnectorSyncSource` (SDK), `ConnectorBinding` (one per resource, rebind replaces), `platform.connector-sync` job (incremental via per-item stamps, fail-closed on every seam), `IConnectorSyncHandler` module seam; legal: `connect_matter_folder`/`sync_matter_folder` → files attach to the matter AND index into its collection. Deferred: scheduled auto-sync (manual/tool-triggered v1) |
| **6** ✅ | Generic-RAG completion: multilingual retrieval, facet filters, per-chunk ACLs, agent knowledge scoping, the curator UI, scale, and the RLS backstop | **Shipped.** Detailed below |
| **7** ✅ | Page-range citations: page-aware extraction (PDF text layer + OCR), offset-tracking chunker, `PageFrom`/`PageTo` through hits and the citation line | **Shipped.** See 4.7 |

---

## Part 4 — Phase 6: what "generic, for any domain and any country" required

Phase 1 built the right *shape* — scoped collections, hybrid retrieval, fail-closed gates. Phase 6
closed the gaps that shape left open once you point it at a real global product (a legal system
serving many countries, thousands of documents per case, confidential material inside shared
matters).

### 4.1 Retrieval is multilingual now

`tsv` shipped as a GENERATED column pinned to `to_tsvector('english', …)`. A generated column cannot
vary per row, so *every* corpus was stemmed as English — Spanish contracts lost recall on every
keyword query, and the product could not honestly claim to work outside English.

- `RagCollection.Language` is the corpus default; `RagChunk.Language` is what each chunk's `tsv` was
  actually built with, because one case holds documents in several languages.
- `tsv` is now written at ingest with the chunk's own configuration (`LanguageDetector`: script
  first — decisive for Cyrillic/Greek/Arabic — then weighted stop-word voting for Latin script).
  It **declines to guess** on thin evidence and falls back to the collection default, then to
  `simple`: a wrong stemmer costs recall on every future query, so not guessing is the better error.
- The lexical arm joins `unnest(languages)` on the chunk's language, giving one *constant*
  `plainto_tsquery` per configuration in scope — a per-row `regconfig` would defeat the GIN index.
- `RagLanguage.Normalize` is the injection guard: only names from the supported set ever reach a
  `regconfig` cast.
- The migration stamps pre-existing rows `english` — what they were genuinely built with — rather
  than letting the new `simple` default silently relabel them.

### 4.2 Three narrowing layers, each fail-closed

| Layer | Mechanism | Answers |
|---|---|---|
| Agent scope | `AgentProfile.CollectionScopes`, globs over `{module}/{resourceType\|-}/{name}` | "this assistant answers from the Spanish employment library and this matter, nothing else" |
| Collection gate | `IRagCollectionGate` per resource type (phase 1) | "who may query this matter's corpus at all" |
| Chunk ACL | `RagChunk.Principals`, set overlap inside **both** arms | "the partner-only memo inside a matter everyone else can see" |

All three only ever *narrow*. Agent scope is applied server-side in `ResolveAccessibleCollectionsAsync`,
so it cannot be escaped by the model choosing a `collection` argument — naming an out-of-scope
collection returns nothing, and enumeration hides it too. Chunk ACLs are opt-in per document (empty
means "the gate already decided") and are re-verified in managed code after fusion.

Principals are opaque strings — `user:`, `role:`, `group:` — compared as a set and never parsed, so
a connector can contribute a source system's own group ids without the retrieval path learning
anything about either identity system. `IRagPrincipalResolver` is the seam; the default contributes
the platform user and their roles. Deliberately **not** permissions: a permission says what you may
do, a principal says who you are, and stamping documents with permissions would make "who can read
this memo" drift every time a role baseline is edited.

### 4.3 Facets are what make it domain-agnostic

`Metadata` (jsonb) on both collection and chunk, filtered with containment inside both arms and
indexed `GIN (metadata jsonb_path_ops)`. The platform never interprets the keys — it only filters on
them. That is the whole trick behind "works for any domain": legal stamps `jurisdiction`/`lawArea`,
property stamps `building`/`unit`, finance stamps `entity`/`fiscalYear`, and retrieval code is
identical.

Filter keys are **discovered from the corpus** rather than declared, so there is no registry to keep
in sync, and `list_knowledge_collections` reports them — an agent asks what it can filter on instead
of guessing.

> Design note: one shared library filtered by `jurisdiction` beats one collection per country.
> Cross-border questions stay answerable in a single query, and a 190-collection fan-out never
> happens.

### 4.4 Scale

- Embeddings and `tsv` are written in batches of 200 via `unnest`, replacing one `UPDATE`
  round-trip *per chunk*. A 3,000-document case is ~60K chunks; the old path was ~60K round trips.
- `RagIndexMaintenance` promotes the table to HNSW once it crosses `Rag:IndexThresholdChunks`
  (default 20,000), pinning the vector column to the dimension in use. Best-effort: an index is an
  optimisation, so a failure is logged and ingestion still succeeds.
- `hnsw.iterative_scan = relaxed_order` is set database-wide by the migration. Without it, an HNSW
  index combined with the tenant/collection/ACL predicates silently loses recall.

### 4.5 The curator surface

Until phase 6, building a corpus meant writing C#. `/api/knowledge` + Admin → Knowledge give a
non-engineer create / configure / index / re-index / delete, a document list, and — most usefully —
a **retrieval preview that runs the identical code path the agent runs**, same gates, same ACLs,
same ranking. Reads are gated exactly like retrieval, so the screen can never show a corpus the
viewer could not search; writes need the new `platform.knowledge.manage`, kept separate from
`platform.ai.manage` because a knowledge curator is a different job from an AI administrator.
Module-owned, resource-bound collections are listed read-only — their lifecycle belongs to their
resource.

### 4.6 RLS, honestly

`FORCE ROW LEVEL SECURITY` policies on both tables, keyed on a `plenipo.tenant_id` session variable
published by `TenantSessionInterceptor` on every connection open. Two caveats stated plainly:

1. **Superusers bypass RLS entirely**, `FORCE` included. A deployment connecting as a superuser
   gets nothing from this layer. The integration test proves the property by dropping to a
   non-superuser role, which is how production must be configured.
2. The policies are **permissive when the setting is unset**, so migrations and cross-tenant
   background scopes are unaffected. A fail-closed policy would be stronger but would turn any path
   that forgot to publish the tenant into an outage instead of a safety net.

Tenant isolation does not depend on RLS — query filters, the explicit predicate in both arms, and
the gates each enforce it. It exists because hybrid search is the one raw-SQL path in the platform.

### 4.7 Page-range citations

A passage now cites the page it came from — "p. 7", or "pp. 3–4" when it straddles a break. Three
pieces had to line up:

1. **Extraction keeps what it already knew.** PdfPig always walked the document page by page; the
   page number was simply discarded. `IDocumentReader.ExtractAsync` returns
   `DocumentText(Text, Pages)` where each `DocumentPage` is a half-open character range into the
   text. `ExtractTextAsync` is unchanged and now delegates to it, so module code is untouched.
2. **Chunks became contiguous slices.** `TextChunker` returns `TextChunk(Text, Start, End)` with
   offsets into the source, and each chunk is now exactly `text[Start..End]` rather than paragraphs
   re-joined with `\n\n`. That is what makes the offsets trustworthy — a chunk stitched from
   non-adjacent pieces could not honestly claim a page range — and it preserves the source
   verbatim as a side effect. The paragraph scanner handles `\n\n` and `\r\n\r\n` without
   normalising, because normalising would shift every offset.
3. **Nulls stay null.** `RagChunk.PageFrom`/`PageTo` are nullable, and a source with no pages
   (plain text) or an extractor that cannot report them cites the file alone. A default of page 1
   would be a fabricated citation — worse than no citation, because it looks checkable.

Scanned documents are covered too: `IOcrEngine.ExtractAsync` is a default-implemented method that
returns unpaged text, so existing engines keep working untouched, and the Azure Document
Intelligence engine overrides it using the page spans its API already returns. That matters in
document-heavy domains where most of the corpus arrives as scans.

Half-open ranges throughout: a chunk ending exactly on a page boundary belongs to the page it came
from, not the one it stops at — otherwise every chunk that ends at a break would over-cite.

### 4.8 Still open

- **No reranker.** Retrieval is hybrid + RRF at `ArmLimit = 50` per arm. A cross-encoder or
  LLM rerank stage is the next real precision win at case scale.
- **Connectors report no ACLs yet.** `ConnectorSyncFile` now *carries* `Principals`/`Metadata` and
  the pipeline stamps them onto chunks, but no shipped source populates them —
  `msgraph` would need Graph's permissions API. The plumbing is done; the sources are not.
- **Scheduled auto-sync** is still manual/tool-triggered.
- **Freshness/supersession.** A repealed statute ranks like a current one. Facets can express
  `effectiveTo`, but nothing ranks on it.
