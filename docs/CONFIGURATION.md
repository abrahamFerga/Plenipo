# Configuring Cortex

This is the single answer to "how is Cortex configured, by whom, and where do secrets go".

## What Cortex is (so the configuration model makes sense)

Cortex is a **base platform, not an application**. It ships as NuGet + npm packages; a *product*
(a "vertical" like the-lawyer) is a thin host that installs modules on top of it. That split drives
the configuration model, because three different people configure three different layers:

| Who | What they decide | Where it lives |
|-----|------------------|----------------|
| **Host developer** (builds the product) | Which modules are installed, AI/embedding provider, skills bundle, storage, auth mode, MCP servers | Code (`AddCortexModule<T>()`) + configuration files (below) |
| **Operator / IT** (deploys it) | Endpoints, API keys, database, identity provider, budgets | Environment variables / user-secrets / Key Vault — never files in the repo |
| **Tenant admin** (runs a firm on it) | Which modules & connectors are on, roles, agent profiles, system prompt, token budgets, connector credentials | The **admin console** (`/admin`) — stored in the database, secrets vault-protected |
| **End user** | Their own connected accounts (e.g. Microsoft 365) | The UI's connect-account flow (OAuth; tokens vault-protected) |

Rule of thumb: **deploy-time shape in configuration, runtime behaviour in the admin console,
secrets never in files.**

## The configuration layers (deploy-time)

Cortex hosts are standard ASP.NET Core apps, so configuration composes in the usual order —
later layers override earlier ones:

1. `appsettings.json` / `appsettings.{Environment}.json` — committed defaults, **no secrets**.
2. **`cortex.settings.json`** — the file the `cortex init` wizard writes (see below). Declarative,
   committed, merged on top of appsettings. This is the OpenClaw-style "one file describes the
   installation" artifact.
3. **Environment variables** — the container/production layer. ASP.NET's `__` convention maps
   sections: `Ai:ApiKey` → `Ai__ApiKey`.
4. **User-secrets** (Development only) — `dotnet user-secrets set "Ai:ApiKey" "sk-..."`.
5. **Azure Key Vault** (Production, optional) — Terraform wires secret references into the
   container app's environment, so the app still just reads configuration.

## Are API keys environment variables? — Yes.

The explicit answer for containers: **the AI API key enters the process as an environment
variable**, and it is never written to any file in the image or the repo.

- **Docker Compose** ([deploy/compose/](../deploy/compose/)): you put `AI_API_KEY=sk-...` in the
  (gitignored) `.env`; the compose file maps it to `Ai__ApiKey` inside the container. Same pattern
  for `AI_PROVIDER`, `AI_MODEL`, `EMBEDDING_PROVIDER`, and `POSTGRES_PASSWORD` (the only mandatory
  one).
- **Local development**: `dotnet user-secrets --project <host> set "Ai:ApiKey" "sk-..."` — outside
  the repo entirely. With the default `Mock` provider no key is needed at all.
- **Azure (Terraform, [infra/](../infra/))**: the key lives in **Key Vault**; the Container App gets
  a secret *reference* which Azure resolves into the `Ai__ApiKey` env var at start. Rotation happens
  in Key Vault, not in a redeploy.

There is a second, separate category: **secrets entered at runtime by non-technical users**
(connector credentials, OAuth tokens, webhook signing secrets). Those never pass through
environment variables — they are entered in the admin UI, stored **write-only** through the
`ISecretVault` seam (DataProtection-encrypted at rest by default; `Secrets:Provider=AzureKeyVault`
switches storage to Key Vault with no migration), and the API only ever reports *that* a value
exists, never the value.

| Secret | How it enters | Where it rests |
|--------|---------------|----------------|
| AI / embedding API key | Env var (`Ai__ApiKey`) / user-secrets / KV reference | Process env only |
| Database password | Env var in connection string | Process env only |
| Connector settings (e.g. storage account key) | Admin UI → Integrations | `ISecretVault` (DataProtection or Key Vault) |
| Per-user OAuth tokens (e.g. Microsoft 365) | User's connect-account flow | `ISecretVault` |
| Notification webhook signing secret | Admin UI → write-only field | `ISecretVault` |
| WhatsApp app secret / access token | user-secrets or env vars | Process env only |

## `cortex init` — the defined way to configure a host

Every deployment is different (different modules, providers, channels, auth), so Cortex defines
**one mechanism** instead of one configuration: the **`cortex` CLI** (`src/Cortex.Cli`), the
platform's analogue of OpenClaw's installer.

```bash
dotnet run --project src/Cortex.Cli -- init --path ./src/MyProduct.Host
```

- An interactive wizard walks the steps (AI provider, knowledge/RAG, document tools, channels,
  file storage, authentication, skills, secret storage); every step can keep the current value.
- Every prompt has a matching flag (`--non-interactive --ai-provider Mock --rag ...`) so CI and
  scripts can run the same thing headlessly.
- It writes **`cortex.settings.json`** next to the host — a declarative, committed file the host
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
| `Ai` | The DEPLOYMENT-DEFAULT chat provider: `Provider` (Mock/OpenAI/AzureOpenAI/Anthropic/Ollama/None), `Model`, `Endpoint`, `ApiKey`, `Temperature`, `MaxOutputTokens`, `MaxConversationTokens`, `MaxMonthlyTokens` | `Mock` is keyless and exercises the full pipeline. Tenants can override the whole connection at runtime (Admin → AI Settings): switch provider/model, bring their own key (vaulted, write-only) — and agent profiles can pin a per-agent model. See [SAAS_OPERATIONS.md](SAAS_OPERATIONS.md). |
| `Rag` | `Enabled`, `EmbeddingProvider`, `EmbeddingModel` | Mock embedder is deterministic and keyless |
| `Skills` | `Enabled`, `Path` | Deploy-time SKILL.md bundles shipped with the host — never tenant uploads |
| `Mcp` | `Servers` — external MCP tool servers (name, transport, command/url, approval) | Deploy-time, like skills; each discovered tool is RBAC-gated as `tools.mcp.*` |
| `Documents` | `Enabled` | Platform PDF/document tools |
| `Files` | `Provider` (Local/AzureBlob) + provider settings | |
| `Channels:WhatsApp` | `Enabled` + Meta Cloud API secrets | Secrets via user-secrets/env |
| `Auth` | `Authority`, `Audience`, `PermissionSource` (Database/Token) | Empty = dev-auth in Development only |
| `Secrets` | `Provider` (DataProtection/AzureKeyVault), `KeyVaultUri` | Where runtime-entered secrets rest |
| `Cors:Origins` | Allowed SPA origins | Aspire injects these automatically in dev |
| `ConnectionStrings` | `cortex-platform`, `cortex-audit`, `cortex-redis` | Env vars in containers |

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
