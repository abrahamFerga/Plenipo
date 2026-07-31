# Configuring Plenipo

This is the single answer to "how is Plenipo configured, by whom, and where do secrets go".

## What Plenipo is (so the configuration model makes sense)

Plenipo is a **base platform, not an application**. It ships as NuGet + npm packages; a *product*
(a "vertical" like the-lawyer) is a thin host that installs modules on top of it. That split drives
the configuration model, because three different people configure three different layers:

| Who | What they decide | Where it lives |
|-----|------------------|----------------|
| **Host developer** (builds the product) | Which modules are installed, AI/embedding provider, skills bundle, storage, auth mode, MCP servers | Code (`AddPlenipoModule<T>()`) + configuration files (below) |
| **Operator / IT** (deploys it) | Endpoints, database, identity provider, budgets, non-chat service credentials | Environment variables / user-secrets / Key Vault — never files in the repo |
| **Tenant admin** (runs a firm on it) | AI provider/model/key, modules, connectors, roles, agent profiles, system prompt, token budgets | The **admin console** (`/admin`) — stored in the database, secrets vault-protected |
| **End user** | Their own connected accounts (e.g. Microsoft 365) | The UI's connect-account flow (OAuth; tokens vault-protected) |

Rule of thumb: **deploy-time shape in configuration, runtime behaviour in the admin console,
secrets never in files.**

## The configuration layers (deploy-time)

Plenipo hosts are standard ASP.NET Core apps, so configuration composes in the usual order —
later layers override earlier ones:

1. `appsettings.json` / `appsettings.{Environment}.json` — committed defaults, **no secrets**.
2. **`plenipo.settings.json`** — the file the `plenipo init` wizard writes (see below). Declarative,
   committed, merged on top of appsettings. This is the OpenClaw-style "one file describes the
   installation" artifact.
3. **Environment variables** — the container/production layer. ASP.NET's `__` convention maps
   ordinary deployment settings such as `Ai:Provider` → `Ai__Provider`.
4. **User-secrets** (Development only) — for deployment services such as RAG, OCR, or channels.
5. **Azure Key Vault** (Production, optional) — Terraform wires secret references into the
   container app's environment, so the app still just reads configuration.

## Are chat-provider API keys environment variables? — No.

OpenAI, Anthropic, and optional Azure OpenAI keys are entered per tenant under **Admin → AI
Settings**. They never pass through deployment configuration. They are stored **write-only** through the
`ISecretVault` seam (DataProtection-encrypted at rest by default; `Secrets:Provider=AzureKeyVault`
switches storage to Key Vault with no migration), and the API only ever reports *that* a value
exists, never the value. Model ids are fetched live from provider catalogs rather than committed as
a static list; Azure OpenAI remains manual because Plenipo needs the resource's deployment name.

| Secret | How it enters | Where it rests |
|--------|---------------|----------------|
| Tenant chat-provider API key | Admin UI → AI Settings | `ISecretVault` (DataProtection or Key Vault) |
| Azure AI Content Safety key | `AgentSecurity__ApiKey` / user-secrets / KV reference (optional when managed identity is available) | Process env only |
| Embedding API key | `Rag__ApiKey` / user-secrets / KV reference | Process env only |
| Database password | Env var in connection string | Process env only |
| Connector settings (e.g. storage account key) | Admin UI → Integrations | `ISecretVault` (DataProtection or Key Vault) |
| Per-user OAuth tokens (e.g. Microsoft 365) | User's connect-account flow | `ISecretVault` |
| Notification webhook signing secret | Admin UI → write-only field | `ISecretVault` |
| WhatsApp app secret / access token | user-secrets or env vars | Process env only |

## `plenipo init` — the defined way to configure a host

Every deployment is different (different modules, providers, channels, auth), so Plenipo defines
**one mechanism** instead of one configuration: the **`plenipo` CLI** (`src/Plenipo.Cli`), the
platform's analogue of OpenClaw's installer.

```bash
dotnet run --project src/Plenipo.Cli -- init --path ./src/MyProduct.Host
```

- An interactive wizard walks the steps (AI provider, knowledge/RAG, document tools, channels,
  file storage, authentication, skills, secret storage); every step can keep the current value.
- Every prompt has a matching flag (`--non-interactive --ai-provider Mock --rag ...`) so CI and
  scripts can run the same thing headlessly.
- It writes **`plenipo.settings.json`** next to the host — a declarative, committed file the host
  layers into its configuration. Re-runs are **non-destructive**: only the keys you decided change,
  anything else (including hand-edits) survives.
- **Secrets are never written** — the wizard prints the `dotnet user-secrets` / env-var commands
  for you to run instead.

What the CLI deliberately does *not* configure: per-tenant runtime choices (module enablement,
roles, agent profiles, connectors). Those belong to the admin console so they can differ per tenant
and change without a deploy.

## Configuration reference (host-level sections)

| Section | Purpose | Notes |
|---------|---------|-------|
| `Ai` | The keyless DEPLOYMENT-DEFAULT chat provider: `Provider` (Mock/AzureOpenAI with managed identity/Ollama/None), `Model`, `Endpoint`, `Temperature`, `MaxOutputTokens`, `MaxConversationTokens`, `MaxMonthlyTokens` | `Mock` exercises the full pipeline. Commercial provider/model/key connections are configured per tenant in Admin → AI Settings; model catalogs are provider-discovered and keys are vaulted write-only. Agent profiles can pin a model. See [SAAS_OPERATIONS.md](SAAS_OPERATIONS.md). |
| `AgentSecurity` | Plenipo-owned agent guardrails plus optional operator-owned Azure AI Content Safety augmentation | `Provider=None/AzureContentSafety`, `Endpoint`, optional `ApiKey`, `DefaultMode=Disabled/Audit/Enforce`, local prompt-attack detection, optional Azure harmful-content screening, sensitive-data handling, severity threshold, fail-closed behavior. Tenant admins choose nullable overrides in Admin → AI Settings. See [AGENT_SECURITY.md](AGENT_SECURITY.md). |
| `Rag` | `Enabled`, `EmbeddingProvider`, `EmbeddingModel`, `DefaultLanguage`, `MaxChunkChars`, `TopK`, `IndexThresholdChunks`, `Reranker`, `RerankCandidateMultiplier`, `MmrLambda`, `RerankerModel` | Mock embedder is deterministic and keyless. `DefaultLanguage` is the Postgres text-search configuration new collections start with — see below. `IndexThresholdChunks` (default 20,000) is when a corpus is promoted from exact scan to an HNSW index. `Reranker` defaults to `Mmr` — see below |
| `Skills` | `Enabled`, `Path` | Deploy-time SKILL.md bundles shipped with the host — never tenant uploads |
| `Mcp` | `Servers` — external MCP tool servers (name, transport, command/url, approval) | Deploy-time, like skills; each discovered tool is RBAC-gated as `tools.mcp.*` |
| `Documents` | `Enabled` | Platform PDF/document tools |
| `Ocr` | `Provider` (None/AzureDocumentIntelligence), `Endpoint`, `ApiKey` | Scanned-PDF/image OCR. Off by default; configuring it lights up the `ocr_document` tool and scanned-statement extraction everywhere the `IOcrEngine` seam is consumed. Key via user-secrets/env (`Ocr__ApiKey`) |
| `Files` | `Provider` (Local/AzureBlob) + provider settings | |
| `Channels:WhatsApp` | `Enabled`, Meta Cloud API secrets, `AllowedSenders`, `AllowUnknownSenders` | Secrets via user-secrets/env; unknown senders denied by default |
| `Channels:Email` | `Enabled`, `Host`/`Port`/`UseSsl`, `Username`, `Password`, `Folder`, `ModuleId`, `TenantSlug`, `PollSeconds`, `ReplyEnabled`, `AllowedSenders`, `AllowUnknownSenders`, `MaxMessageBytes` | IMAP intake mailbox polled into agent turns (docs/INBOUND_CHANNELS.md); password via user-secrets/env; replies and unknown senders off by default |
| `Email` | Outbound SMTP: `Enabled`, `Host`/`Port`/`UseStartTls`, `Username`, `Password`, `FromAddress`, `FromName` | Powers the email notification channel AND user invites; password via user-secrets/env. Unconfigured, invites still work (share the link manually) |
| `Push` | Mobile push: `Enabled`, `IncludeContent`, `PlaceholderTitle`/`PlaceholderBody`, `ExpoEndpoint`, `ExpoAccessToken`, `MaxDevicesPerUser` | Nothing to configure for most deployments — the channel is inert until a device registers, and the built-in Expo transport needs no Apple/Google credentials. **`IncludeContent=false`** is the one to think about: see below |
| `Auth` | `Authority`, `Audience`, `ClientId`, `Scopes`, `PermissionSource` (Database/Token), `TenantClaim` (default `tenant`) | Empty = dev-auth in Development only. `ClientId`/`Scopes` are what the BROWSER signs in with — see below |
| `Bootstrap` | `TenantSlug`, `TenantName`, `AdminEmail`, `AdminSubject`, `AdminRoles` | **First run only** — creates the deployment's first tenant and its operator. Consumed at startup, never over HTTP, inert once any operator exists. See below |
| `Secrets` | `Provider` (DataProtection/AzureKeyVault), `KeyVaultUri` | Where runtime-entered secrets rest |
| `DataProtection:KeysPath` | Shared durable directory for the Data Protection key ring | Optional alternative to `plenipo-redis`; required outside Development when Redis is absent |
| `Security:OutboundUrls` | `AllowHttp`, `AllowPrivateNetworks` | Both false by default; applies to tenant-configured webhooks, AI endpoints, OAuth and connector URLs |
| `Cors:Origins` | Allowed SPA origins | Aspire injects these automatically in dev |
| `ConnectionStrings` | `plenipo-platform`, `plenipo-audit`, `plenipo-redis` | Env vars in containers |
| `Connectors:Exclude` | Connector ids to suppress deployment-wide, e.g. `["s3","documenso"]` | Removes a compiled-in connector without recompiling; see below |
| `Connectors:OperatorEnabled` | Map of restricted connector ids to explicit operator approval | The `local-folder` connector is absent unless `Connectors:OperatorEnabled:local-folder=true`; also set `Connectors:LocalFolder:AllowedRoots` |
| `Modules:Exclude` | Module ids to suppress deployment-wide | Unlike the per-tenant toggle, exclusion removes endpoints/tools/catalog entry entirely |

### Wiring MCP tool servers

MCP servers are deploy-time configuration (like skills): the host operator declares them, and every
discovered tool flows through the normal security spine — named `{server}_{tool}`, RBAC-gated as
`tools.mcp.{server}_{tool}` (granted to **no role** by default; an admin opts users in, or grants
`tools.mcp.*`), audited, and **approval-gated by default** (opt a read-only server out with
`RequiresApproval: false`). An unreachable server just means its tools aren't offered — never a
failed start or chat turn.

```jsonc
// plenipo.settings.json or appsettings — Stdio (subprocess) or Http (Streamable HTTP)
"Mcp": {
  "Servers": [
    { "Name": "github", "Transport": "Stdio", "Command": "npx",
      "Arguments": ["-y", "@modelcontextprotocol/server-github"] },
    { "Name": "search", "Transport": "Http", "Url": "https://mcp.example.com", "RequiresApproval": false }
  ]
}
```

### Connectors: what a deployment offers vs. what a tenant uses

Two dials, deliberately separate. **What the deployment offers** is code + config: the built-in
bundle registers in one line (`builder.AddPlenipoConnectors()`), any package's connectors register
with `AddPlenipoConnectorsFrom(assembly)`, and `Connectors:Exclude` suppresses any of them without
recompiling. **What a tenant uses** is the admin's runtime, default-off toggle on the Integrations
page — enabling a connector there is what makes its tools exist for that tenant, each still
RBAC-gated and audited. The Integrations page also lists first-party connectors the deployment
did NOT install (with the package + registration call), so discovering an integration never
requires reading platform source. `Modules:Exclude` works the same way for domain modules.

The host-filesystem `local-folder` connector has an additional deployment boundary because tenant paths
must never grant arbitrary server reads. It is not registered until the operator enables it, and every
tenant-selected root must be contained by one of the operator-owned `AllowedRoots`. Reparse points and
symlinks are refused while walking the tree.

### Knowledge (RAG): language, scale, and the RLS caveat

`Rag:Enabled` is off by default and the whole subsystem is opt-in — a deployment that doesn't need
retrieval registers nothing and offers no tool. Once on, three settings matter beyond the embedding
provider.

**`Rag:DefaultLanguage`** is the Postgres text-search configuration new collections start with. It
defaults to `simple`, which stems nothing and stops nothing: slightly weaker recall, but never the
*wrong* language's stemmer — the right default for a deployment serving several countries. A
single-language deployment should set its own (`english`, `spanish`, `german`, …). Every collection
can override it in Admin → Knowledge, and each document is language-detected at index time, so a
mixed-language corpus indexes each file with its own stemmer. Only configurations bundled with a
stock Postgres are accepted; anything unknown falls back to `simple` rather than failing.

> CJK corpora index as `simple` on purpose. Postgres ships no CJK segmenter, so a configuration
> would produce one enormous token; keyword matching degrades and the vector arm carries those
> corpora. Deployments that need CJK keyword search should add `pgroonga` or `pg_bigm`.

**`Rag:IndexThresholdChunks`** (default 20,000) is where a corpus stops using exact scan — perfect
recall, no index to maintain — and gets an HNSW index instead. The promotion happens once, inside
the ingest job that crossed the threshold, and it pins the vector column to the embedding
dimension in use. Changing to an embedding model with different dimensions therefore means dropping
the index and re-embedding, which is what a model migration already required.

**`Rag:Reranker`** is the precision pass over the retrieved shortlist. Retrieval is deliberately
recall-oriented — it casts a wide net and fuses two arms that disagree about what "similar" means —
and reranking is what turns that shortlist into an ordering worth showing.

| Value | Cost | What it does |
|---|---|---|
| `Mmr` (default) | arithmetic only | Maximal marginal relevance over the candidates' own vectors. Stops a window of eight results from being eight copies of the same boilerplate clause. Deterministic. |
| `Llm` | one model call per search | The tenant's chat model scores each passage against the query (cross-encoder). The most accurate option; costs latency and tokens. |
| `None` | none | Fusion order, truncated — the behaviour before reranking existed. |

`RerankCandidateMultiplier` (default 5) sets how deep the shortlist is: a reranker can only promote
what retrieval fetched, so `TopK=8` pulls 40 candidates and returns the best 8. The product is
capped at 100. `MmrLambda` (default 0.7) is the relevance/diversity trade-off — 1.0 is pure
relevance, 0.0 pure diversity. At 0.7 diversity only breaks near ties, so a merely *different*
passage never outranks a much more relevant one.

> Upgrading from a build before reranking existed changes result ordering, because `Mmr` is on by
> default. Set `Rag:Reranker=None` to restore the previous ordering exactly.

The `Llm` reranker fails soft in every direction — no provider, a refusal, malformed output — and
falls back to retrieval order rather than failing the search, so enabling it cannot take retrieval
down. It uses the tenant's own AI connection, so per-tenant metering and BYO keys apply to it the
same way they apply to chat. Set `RerankerModel` to pin a cheaper/faster model than the tenant's
chat default.

**Row-level security is a real backstop only on a non-superuser connection.** The retrieval tables
carry `FORCE ROW LEVEL SECURITY` policies keyed on the session's `plenipo.tenant_id`, which the
platform publishes on every connection open. PostgreSQL **superusers bypass RLS entirely**, `FORCE`
included — so a deployment whose connection string uses a superuser (the default for a local
container, and for some small managed instances) gets no protection from this layer. Connect as an
ordinary role that owns nothing to make it effective. Tenant isolation does not *depend* on this:
EF's global query filters, the explicit tenant predicate inside both retrieval arms, and the
collection gates all enforce it independently. RLS exists because hybrid search is the one place
the platform writes raw SQL, and a mistake there would be cross-tenant.

The policies are permissive when `plenipo.tenant_id` is unset, so migrations, ops tooling, and
background scopes that legitimately span tenants are unaffected. That is deliberate: a fail-closed
policy would turn any code path that forgot to publish the session tenant into an outage rather
than a defence-in-depth layer.

### Product identity (Branding)

The shell asks the host who it is at runtime — one prebuilt UI bundle serves every product:

```jsonc
// appsettings.json (not a secret)
"Branding": { "ProductName": "Networthy" }   // -> GET /api/platform/branding -> top bar + tab title
```

### Billing (Commerce) — off by default

A deployment that doesn't sell subscriptions has no webhook surface at all. Selling turns on via
the `Commerce` section (secrets via user-secrets/Key Vault, never appsettings): `Enabled`,
`WebhookSecret` (SECRET), `StripeApiKey` (SECRET), `Prices:{product}:{plan}` -> Stripe Price ids,
`CheckoutSuccessUrl`/`CheckoutCancelUrl`, and `Dedicated:{Owner,Repo,Workflow,Token(SECRET)}` for
the dedicated-environment tier. The flow (checkout -> signed webhook -> durable inbox -> one-
transaction tenant provisioning) is platform machinery; a product only declares its
`ProductOffering` in the host. See a worked operator checklist in networthy's `docs/HOSTED.md`.

### First-run setup wizard (Onboarding)

Declared per module in the manifest (`ModuleManifest.Onboarding`): a probe endpoint ("do I have
data yet?"), a permission, and info/form/upload steps. No host configuration — the shell renders
the wizard and offers it via a dismissible banner while the probe returns an empty array.

### Admin console extension pages (AdminTabs)

Modules can contribute pages to the **admin console** the same way they contribute domain tabs:
declare `ModuleManifest.AdminTabs` (the same `TabDescriptor` machinery — data table, editor,
chart, actions) and the admin app renders them under the module's name, no `@plenipo/admin-ui`
fork needed. Every admin tab must declare a `Permission` (validated at startup) — an admin
surface is never visible by default. Served permission-filtered at `GET /api/admin/extensions`.

### Inviting people (Admin → Users)

Plenipo provisions users just-in-time at first sign-in — which used to mean roles could only be
assigned to people who had already signed in once. **Standing invites** close that gap: an admin
names an email address and starting roles, and the first sign-in with that address applies them
automatically (any IdP — the invite is keyed on the email claim, no token link). With `Email`
configured the invitee gets a mail; without it the invite still works and the admin shares the
sign-in link. Pending invites are revocable; everything is audited.

### Mobile push: what leaves the deployment

The push channel is on by default and does nothing until someone registers a device from the mobile
shell, so a deployment with no mobile app configures nothing.

The decision worth making deliberately is **`Push:IncludeContent`**. It defaults to `true`, which
sends the notification's title and body to the push service — a third party — where they also land
on a lock screen readable by anyone holding the phone. For a deployment handling privileged
material (a legal matter, a diagnosis, a client's finances), set it to `false`:

```jsonc
"Push": { "IncludeContent": false }
```

The device then receives only "New notification"; tapping through fetches the real content over the
app's authenticated session. The category and the deep link still travel — they are routing, not
content.

Two things are always true regardless: a push token is never echoed back by any endpoint (including
the caller's own device list), and a token the push service reports as permanently gone is deleted
rather than kept.

To use your own FCM/APNs credentials or a corporate gateway instead of Expo, replace the transport
with one DI registration — `services.AddSingleton<IPushTransport, MyTransport>()` — and nothing
above it changes. See [MOBILE.md](MOBILE.md).

### Per-user notification preferences

Modules declare the notification categories they emit (`ModuleManifest.NotificationCategories`),
and every user gets a per-category mute switch in the notification bell. A mute suppresses that
category entirely for that user — the in-app row and every channel — without touching anyone
else's notifications or any other category. No stored row means "on", so new categories need no
backfill.

## Signing in from a browser

Setting `Auth:Authority` + `Auth:Audience` secures the **API**. To let a person sign in from the shipped
web UI, add the public client id of your SPA app registration:

```jsonc
{
  "Auth": {
    "Authority": "https://your-tenant.ciamlogin.com/<tenant-id>/v2.0",
    "Audience":  "api://your-api-id",
    "ClientId":  "<the SPA app registration's client id>",
    "Scopes":    "api://your-api-id/.default"   // provider-specific; omit if unsure
  }
}
```

Register these redirect URIs with the identity provider:

| Surface | Redirect URI |
|---|---|
| App (`/`) | `https://<your-host>/signin-callback` |
| Admin console (`/admin`) | `https://<your-host>/admin/signin-callback` |

The shell learns all of this at runtime from the anonymous `GET /api/platform/auth-config`, which is what
lets **one prebuilt bundle serve every deployment** — nothing is baked in at build time. It then runs
Authorization Code + PKCE with no client secret (a browser cannot keep one), holds the access token in
memory, and offers a **Sign in** button when the API answers 401.

`Auth:ClientId` is deliberately optional and does **not** fail startup when missing: an existing API-only
deployment must keep starting after an upgrade. A browser that finds no client id says so on screen
instead of looping on 401.

A product with its own identity stack skips all of this and supplies an adapter:

```tsx
import { PlenipoApp, type AuthAdapter } from "@plenipo/ui";

const auth: AuthAdapter = { getAccessToken: () => myIdentity.getToken() };
createRoot(el).render(<PlenipoApp config={{ auth }} />);
```

That is the same `AuthAdapter` shape `@plenipo/mobile` takes, so a product that has wired one already
knows this one. With no authority configured the shell keeps using the Development-only `X-Dev-*`
headers, so a local host still works with nothing configured at all.

## First run outside Development: the `Bootstrap` section

Development seeds a `dev` tenant automatically. **Nothing else does.** So a fresh Production database
starts empty — and because a request's permissions are only resolved *after* its tenant resolves, every
principal on a tenant-less deployment carries an empty permission set. That includes one asserting
`system_admin`, so `POST /api/admin/tenants` — the endpoint that would fix it — is unreachable. It is a
deadlock, not a permissions problem.

`Bootstrap` breaks it once, from configuration:

```jsonc
{
  "Bootstrap": {
    "TenantSlug":   "acme",              // required; lowercase letters, digits, hyphens
    "TenantName":   "Acme Ltd",          // optional; defaults to the slug
    "AdminEmail":   "admin@acme.test",   // required
    "AdminSubject": "8f3c…",             // the `sub` your IdP will present for this person
    "AdminRoles":   ["system_admin"]     // optional; this is the default
  }
}
```

In environment-variable form each array entry is its own key: `Bootstrap__AdminRoles__0=tenant_admin`.
Setting any entry **replaces** the default rather than adding to it.

What it does and does not do:

- **Not an HTTP surface, ever.** An operator who can set configuration already controls the deployment;
  naming the first admin adds no authority they did not have. There is no anonymous first-run door and no
  default password.
- **Self-disarming.** It no-ops the moment any principal in the deployment holds an operator-reserved
  permission — not merely when a tenant exists, because a commerce-provisioned tenant gets a
  `tenant_admin`, who deliberately cannot create tenants. **Remove the section after the first successful
  start.**
- **Audited** as `PlatformBootstrapped`, with the tenant, the admin and the roles granted.
- **`AdminSubject` is required whenever `AdminRoles` grants an operator-reserved permission.** Without a
  subject the roles are bound through a standing invite keyed on the email address, and the platform
  matches email from an unverified token claim — fine for tenant-grade roles, not for cross-tenant control.
- With `Auth:PermissionSource=Token` the tenant is still created, but the roles are inert: the IdP is the
  only source of roles, and it must assert them. The host logs a warning saying so.

**After bootstrapping**, the token's tenant claim (`Auth:TenantClaim`, default `tenant`) must carry the
slug. A single-tenant deployment is resolved by fallback even without the claim, but the moment a second
tenant exists that fallback stops working — configure the claim before you get there.

If a request's tenant does not resolve, `GET /api/platform/me` reports `tenantResolved: false` with a
`tenantProblem` naming the cause, and the resulting 403 carries the same explanation in its body.

## Where runtime configuration lives (admin console, per tenant)

Everything below is stored in the database, editable at `/admin` without a deploy, and RBAC-gated:

- **Modules** — enable/disable installed modules per tenant.
- **Agent Profiles** — named chatbot configurations per module: instructions
  (append/replace), which is default, which tools the agent may use, and its own model.
- **AI Settings** — the tenant's provider connection (switch provider/model at runtime; API key
  vaulted write-only), tenant system prompt, per-conversation and monthly token budgets, and agent
  security policy (audit/enforce, local prompt-attack detection, optional harmful-content screening,
  sensitive-data redact/block).
- **Integrations** — connector enablement + credentials (vault-protected, write-only).
- **Roles / Users / Security** — the runtime-editable RBAC baselines and the live permission map.
- **Notifications** — webhook delivery + signing secret.

## Related reading

- [TESTING.md](TESTING.md) — how to run and test the base platform itself.
- [../deploy/compose/README.md](../deploy/compose/README.md) — single-box Docker deployment.
- [../infra/README.md](../infra/README.md) — Azure deployment via Terraform.
- [../GETTING_STARTED.md](../GETTING_STARTED.md) — clone → running demo in three steps.
