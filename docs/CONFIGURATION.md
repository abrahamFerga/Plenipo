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
| `Rag` | `Enabled`, `EmbeddingProvider`, `EmbeddingModel` | Mock embedder is deterministic and keyless |
| `Skills` | `Enabled`, `Path` | Deploy-time SKILL.md bundles shipped with the host — never tenant uploads |
| `Mcp` | `Servers` — external MCP tool servers (name, transport, command/url, approval) | Deploy-time, like skills; each discovered tool is RBAC-gated as `tools.mcp.*` |
| `Documents` | `Enabled` | Platform PDF/document tools |
| `Ocr` | `Provider` (None/AzureDocumentIntelligence), `Endpoint`, `ApiKey` | Scanned-PDF/image OCR. Off by default; configuring it lights up the `ocr_document` tool and scanned-statement extraction everywhere the `IOcrEngine` seam is consumed. Key via user-secrets/env (`Ocr__ApiKey`) |
| `Files` | `Provider` (Local/AzureBlob) + provider settings | |
| `Channels:WhatsApp` | `Enabled`, Meta Cloud API secrets, `AllowedSenders`, `AllowUnknownSenders` | Secrets via user-secrets/env; unknown senders denied by default |
| `Channels:Email` | `Enabled`, `Host`/`Port`/`UseSsl`, `Username`, `Password`, `Folder`, `ModuleId`, `TenantSlug`, `PollSeconds`, `ReplyEnabled`, `AllowedSenders`, `AllowUnknownSenders`, `MaxMessageBytes` | IMAP intake mailbox polled into agent turns (docs/INBOUND_CHANNELS.md); password via user-secrets/env; replies and unknown senders off by default |
| `Email` | Outbound SMTP: `Enabled`, `Host`/`Port`/`UseStartTls`, `Username`, `Password`, `FromAddress`, `FromName` | Powers the email notification channel AND user invites; password via user-secrets/env. Unconfigured, invites still work (share the link manually) |
| `Auth` | `Authority`, `Audience`, `PermissionSource` (Database/Token) | Empty = dev-auth in Development only |
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

### Per-user notification preferences

Modules declare the notification categories they emit (`ModuleManifest.NotificationCategories`),
and every user gets a per-category mute switch in the notification bell. A mute suppresses that
category entirely for that user — the in-app row and every channel — without touching anyone
else's notifications or any other category. No stored row means "on", so new categories need no
backfill.

## Where runtime configuration lives (admin console, per tenant)

Everything below is stored in the database, editable at `/admin` without a deploy, and RBAC-gated:

- **Modules** — enable/disable installed modules per tenant.
- **Agent Profiles** — named chatbot configurations per module: instructions
  (append/replace), which is default, which tools the agent may use, and its own model.
- **AI Settings** — the tenant's provider connection (switch provider/model at runtime; API key
  vaulted write-only), tenant system prompt, per-conversation and monthly token budgets.
- **Integrations** — connector enablement + credentials (vault-protected, write-only).
- **Roles / Users / Security** — the runtime-editable RBAC baselines and the live permission map.
- **Notifications** — webhook delivery + signing secret.

## Related reading

- [TESTING.md](TESTING.md) — how to run and test the base platform itself.
- [../deploy/compose/README.md](../deploy/compose/README.md) — single-box Docker deployment.
- [../infra/README.md](../infra/README.md) — Azure deployment via Terraform.
- [../GETTING_STARTED.md](../GETTING_STARTED.md) — clone → running demo in three steps.
