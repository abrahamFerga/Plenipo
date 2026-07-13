# Cortex — single-box deployment (Docker Compose)

The simplest production-shaped way to run Cortex: one compose file, four containers
(API + independently credentialed platform and audit Postgres + Redis), all state in named volumes. Modeled on what makes
OpenClaw-style deployments dependable — pinned versions, one state boundary, and an
upgrade that is exactly two commands.

## Quickstart

```bash
cd deploy/compose
cp .env.example .env         # set both DB passwords plus AUTH_AUTHORITY and AUTH_AUDIENCE
docker compose up -d --build
```

Open http://localhost:8080/alive — `200 OK` means the stack is up. The default is
production mode with JWT authority and audience validation. For a local-only keyless demo,
explicitly set `CORTEX_ENVIRONMENT=Development`; dev header auth
(`X-Dev-Subject` / `X-Dev-Tenant` / `X-Dev-Roles`) is never available in production. Then chat via AG-UI:

```bash
curl -N -X POST http://localhost:8080/api/agui/finance \
  -H "Content-Type: application/json" \
  -H "X-Dev-Subject: dev-user" -H "X-Dev-Tenant: dev" -H "X-Dev-Roles: system_admin" \
  -d '{"messages":[{"id":"m1","role":"user","content":"How much did I spend on groceries?"}]}'
```

The React front-ends (`@abrahamferga/cortex-ui`, `@cortex/admin-ui`) run separately for now
(`pnpm -C frontend dev` with `VITE_API_BASE=http://localhost:8080`); a bundled UI
container is on the roadmap.

## Upgrades

Pin an image tag in `.env` (`CORTEX_IMAGE=...:0.2.0`) so updates are deliberate, then:

```bash
docker compose pull && docker compose up -d
```

Database migrations run automatically at API startup. Postgres MAJOR upgrades
(pg17 → pg18) are the exception: dump/restore, don't just bump the tag — the data
volume layout is major-specific.

## State & backup

Everything the deployment owns lives in three named volumes plus your `.env`:

| What | Where |
|---|---|
| Operational data and RAG index | `cortex-pgdata` (`cortex_platform`) |
| Append-only audit trail | `cortex-audit-pgdata` (`cortex_audit`, separate credentials) |
| Cache, SignalR backplane, Data Protection key ring | `cortex-redisdata` (**not safe to lose**) |
| Deployment identity & keys | `.env` (never commit it) |

Back up both Postgres services and the Redis AOF on a schedule, plus a copy of `.env`.
Restore all three state volumes onto any Docker host and you have the same
system — this is the multi-cloud-friendly path: the same compose file runs on an Azure
VM, AWS EC2, or a box under the desk. (Cloud-managed topology — Container Apps, managed
Postgres, Key Vault — is `deploy/terraform/`.)

## Going real (beyond the demo)

| Concern | What to change |
|---|---|
| AI provider | Configure each tenant's provider/model/key in Admin → AI Settings. Keys are vaulted and write-only; model choices are loaded live from providers. |
| Authentication | Keep `CORTEX_ENVIRONMENT=Production` and set both `AUTH_AUTHORITY` and `AUTH_AUDIENCE` for the external IdP (the `X-Dev-*` scheme exists only in Development) |
| Host filesystem connector | Explicitly set `Connectors__OperatorEnabled__local-folder=true` and one or more `Connectors__LocalFolder__AllowedRoots__N` values; otherwise it is not registered |
| Connector secrets | Set in the admin UI (write-only). To store them in Azure Key Vault instead of the DB: `Secrets__Provider=AzureKeyVault` + `Secrets__KeyVaultUri=...` on the api service |
| TLS / domain | Put a reverse proxy (Caddy, Traefik, nginx) in front of port 8080 |
| Skills | Mount or bake a skills directory; `Skills__Path` points at it |
| MCP tool servers | Deploy-time like skills: `Mcp__Servers__0__Name=... Mcp__Servers__0__Transport=Stdio|Http ...` on the api service; each discovered tool is RBAC-gated as `tools.mcp.*` (granted to no role by default) and approval-gated unless the server opts out |
