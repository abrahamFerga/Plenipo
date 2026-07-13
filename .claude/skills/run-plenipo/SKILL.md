---
name: run-plenipo
description: >
  Run, observe, and test the Plenipo AI platform locally. Use when asked to start
  Plenipo, run it under Aspire, read its logs/telemetry, exercise the chat
  assistant (AG-UI or SignalR), drive the admin/RBAC/usage dashboard, or run the
  React UI — and to verify a change actually works at runtime, not just compiles.
  Covers two launch modes: full-stack Aspire (with the dashboard + MCP) and a
  headless throwaway-Postgres mode for scripted/CI verification. The chat
  assistant works with zero configuration via the built-in "Mock" provider.
---

# Running & testing Plenipo

Plenipo is a **base platform** (`src/`, shipped as NuGet/npm packages) plus
**sample apps** (`samples/`). The runnable demo is the **sample app**, which
installs the Finance + Nutrition modules onto the platform.

> The platform host `src/Plenipo.AppHost` runs `Plenipo.Api`, which is a *bare
> shell with no domain modules* — chat there has nothing to talk to. For a
> working chatbot always run the **sample** app host below.

## Prerequisites

- **Docker Desktop** running (Postgres + Redis run as containers)
- **.NET 10 SDK** (`dotnet --version` ≥ 10)
- **Node 20+** for the frontend
- Aspire is pulled in via the AppHost SDK; no separate workload install needed

## AI provider — works with zero config

The chat assistant defaults to the **`Mock`** provider
(`samples/Plenipo.Sample.Host/appsettings.Development.json` → `Ai:Provider=Mock`).
The mock streams a deterministic, contextual reply and reports token usage, so
the full pipeline (streaming, persistence, usage tracking, AG-UI, SignalR) works
with **no API key**. Configure a commercial connection per tenant under Admin → AI Settings;
the key is vaulted write-only and the model list is fetched live from the provider.

## Mode A — full stack under Aspire (dashboard + MCP)

```powershell
corepack enable; pnpm --dir frontend install   # once — deps for the UI resources
dotnet run --project samples/Plenipo.Sample.AppHost
```

This provisions Postgres (platform + audit DBs) and Redis as containers, runs
the sample API wired to them, **and launches both front-ends as Vite dev-server
resources** (`plenipo-ui` = domain UI, `plenipo-admin-ui` = admin console), then
opens the **Aspire dashboard** (URL printed in the console, usually
`https://localhost:17xxx`). The dashboard shows every resource's console logs,
structured logs, traces, and metrics — and each UI's external URL. Aspire injects
`VITE_API_BASE` into the UIs and adds their origins to the API's CORS policy, so
the UIs reach the API with no manual pointing.

The AppHost default is the keyless Mock provider. Commercial connections are configured per tenant
after startup under Admin → AI Settings.

**Reading logs/telemetry via the Aspire MCP.** If an Aspire MCP server is
connected (tools surface via ToolSearch — search `aspire`; `.mcp.json` registers
`aspire mcp start`), use it to list resources, tail logs, and read traces without
scraping the console. The MCP/CLI is the agent-readable view of the same
OpenTelemetry the dashboard renders. Gotchas that make it report "No Aspire
AppHost is currently running":

- The AppHost must be launched with **`aspire run`** (the CLI backchannel) — an
  AppHost started via `dotnet run` is invisible to the MCP.
- The **CLI and AppHost SDK versions must match** (e.g. CLI 13.1 cannot see a
  13.4 AppHost). Update with the official installer: `iex "& { $(irm https://aspire.dev/install.ps1) }"`.
- Stale zero-byte `~/.aspire/cli/backchannels/aux.sock.*` files from crashed
  sessions break discovery — delete them.
- Discovery is push-based and takes a few seconds after the MCP server starts.

If no MCP is connected to the session, you can still drive one over stdio:
spawn `aspire mcp start --non-interactive`, speak JSON-RPC (initialize →
tools/call), and call `list_resources` / `list_console_logs` /
`list_structured_logs`. **Dashboard-reading tip:** resources named
`*-installer` (pnpm install helpers, run to "Finished" each start) and
`*-rebuilder` (on-demand rebuild, stays "NotStarted") are helpers — not
services failing to start.

Caveat: in a headless/cron run the dashboard and MCP may be unavailable — use
Mode B there.

The API's external HTTP endpoint is shown in the dashboard (resource
`plenipo-sample`). Use that base URL for the API calls below.

## Mode B — headless (scripted / CI / no dashboard)

This is the reliable path for automated verification. It uses a throwaway
Postgres container and runs the built API directly. **Critical gotcha:** launch
the API with its **working directory set to the build output**, or ASP.NET's
ContentRoot won't find `appsettings.Development.json` and the chat provider
silently falls back to `None` ("AI provider is not configured").

```powershell
# 1) Build, then start throwaway Postgres
dotnet build samples/Plenipo.Samples.slnx
docker rm -f plenipo-pg-test 2>$null
docker run -d --name plenipo-pg-test -e POSTGRES_PASSWORD=postgres -p 5432:5432 pgvector/pgvector:pg16

# 2) Run the sample host — WorkingDirectory MUST be the bin folder
$bin = "samples/Plenipo.Sample.Host/bin/Debug/net10.0"
$pg  = "Host=127.0.0.1;Port=5432;Database={0};Username=postgres;Password=postgres"
$env:ASPNETCORE_ENVIRONMENT = "Development"
$api = Start-Process dotnet -WorkingDirectory $bin -PassThru -ArgumentList @(
  "$PWD/$bin/Plenipo.Sample.Host.dll",
  "--ConnectionStrings:plenipo-platform=$($pg -f 'plenipo_platform')",
  "--ConnectionStrings:plenipo-audit=$($pg -f 'plenipo_audit')",
  "--urls=http://127.0.0.1:8094")

# 3) Wait for readiness (liveness endpoint never calls the LLM)
1..60 | ForEach-Object { Start-Sleep 2; try { if ((iwr http://127.0.0.1:8094/alive -UseBasicParsing).StatusCode -eq 200) { "ready"; break } } catch {} }

# ... run the tests below against http://127.0.0.1:8094 ...

# 4) Teardown
Stop-Process -Id $api.Id -Force; docker rm -f plenipo-pg-test
```

## Dev authentication

No IdP is needed in Development. Send these headers on every API call (the SPA's
`devAuth.ts` sends them automatically):

```
X-Dev-Subject: dev-user
X-Dev-Tenant:  dev
X-Dev-Roles:   system_admin     # system_admin holds the "*" permission
```

## Test the chatbot (the core feature)

**AG-UI protocol** (open standard: HTTP POST + SSE):

```powershell
$h = @{ "X-Dev-Subject"="dev-user"; "X-Dev-Tenant"="dev"; "X-Dev-Roles"="system_admin" }
$body = @{ messages = @(@{ id="m1"; role="user"; content="How much did I spend on groceries?" }) } | ConvertTo-Json
$r = Invoke-WebRequest "http://127.0.0.1:8094/api/agui/finance" -Method Post -Headers $h `
       -ContentType "application/json" -Body $body -UseBasicParsing
($r.Content -split "`n") | Where-Object { $_ -like "data:*" }
```

A working turn streams this event sequence:
`RUN_STARTED` → `TEXT_MESSAGE_START` → many `TEXT_MESSAGE_CONTENT` (deltas) →
`CUSTOM` (name `token_usage`) → `TEXT_MESSAGE_END` → `RUN_FINISHED`.
If you only see `RUN_STARTED` + `RUN_ERROR "AI provider is not configured"`, the
dev appsettings didn't load — re-check the **WorkingDirectory** gotcha above.

**SignalR** is the other transport (hub at `/hubs/agent`, method `Stream`); the
React `ChatPanel` uses it. Both go through the same authorized, audited runner.

Module ids to chat against: `finance`, `nutrition`, `legal`.

## Test the admin / security / usage features

```powershell
$b = "http://127.0.0.1:8094"
Invoke-RestMethod "$b/api/platform/modules"          -Headers $h   # installed modules + tabs
Invoke-RestMethod "$b/api/admin/security/catalog"    -Headers $h   # permission map: platform + every module tool
Invoke-RestMethod "$b/api/admin/roles"               -Headers $h   # roles -> baseline permissions
Invoke-RestMethod "$b/api/admin/users"               -Headers $h   # users with roles + grants
Invoke-RestMethod "$b/api/admin/usage?days=30"       -Headers $h   # token usage (populated after a chat turn)
Invoke-RestMethod "$b/api/admin/audit/tool-calls"    -Headers $h   # agent tool-call audit

# Grant a tool permission to a user (RBAC mutation):
$uid = (Invoke-RestMethod "$b/api/admin/users" -Headers $h)[0].id
Invoke-RestMethod "$b/api/admin/users/$uid/permissions" -Method Post -Headers $h `
  -ContentType "application/json" -Body (@{ permission="tools.finance.summarize_spending" } | ConvertTo-Json)
```

## Run the React UI

```powershell
corepack enable                                  # pnpm on PATH (frontend is a pnpm workspace)
pnpm -C frontend install
$env:VITE_API_BASE = "http://127.0.0.1:8094"     # point at the running API
pnpm -C frontend dev                             # @plenipo/ui on http://localhost:5173
```

The shell renders modules/tabs from `/api/platform/modules`, chats over SignalR,
and (for platform admins) shows an **Admin** area: Security map, Users & Roles,
Token Usage, Audit Log. Ensure the API's `Cors:Origins` includes the SPA origin
(`http://localhost:5173` by default).

## Test the WhatsApp channel (no Meta account needed)

The WhatsApp channel (Meta Cloud API webhook → authorized agent turns; see
`docs/WHATSAPP_CHANNEL.md`) is **off by default** and fully covered by keyless
E2E tests — the Mock provider answers the turn and a capturing fake replaces
the Cloud API sender:

```powershell
dotnet test samples/Plenipo.Sample.Host.IntegrationTests --filter FullyQualifiedName~WhatsApp
```

To poke it manually, enable `Channels:WhatsApp:*` via dev settings and POST a
signed payload — `WhatsAppSignature.Compute` (in `Plenipo.AspNetCore`) produces
the `X-Hub-Signature-256` value, and `plenipo.http` has a ready-made request pair.

## Verify the build & tests

```powershell
dotnet build Plenipo.slnx; dotnet build samples/Plenipo.Samples.slnx
dotnet test  Plenipo.slnx; dotnet test  samples/Plenipo.Samples.slnx
pnpm -C frontend install; pnpm -C frontend -r lint; pnpm -C frontend -r test; pnpm -C frontend build:all
```

## Common gotchas

| Symptom | Cause / fix |
|---|---|
| Chat returns `RUN_ERROR "AI provider is not configured"` | ContentRoot didn't load dev appsettings — set Start-Process **WorkingDirectory** to the bin folder, or pass `--Ai:Provider=Mock` on the command line |
| `RUN_ERROR "Unknown module"` | Module id must be `finance` or `nutrition`; the bare `src/Plenipo.AppHost` has no modules — use the sample AppHost |
| Startup fails on DB connect | Docker not running, or Postgres not ready yet — wait for `pg_isready` before launching the API |
| Aspire: containers up but the API never starts (stack "hangs" after the dashboard banner) | Stale Postgres **data volume** initialized with a different generated password than the AppHost user-secrets now hold — `docker logs <plenipo-pg-…>` shows `password authentication failed`, health checks never pass, `WaitFor` blocks the API + UIs forever. Fix: `docker volume ls | grep plenipo` → `docker volume rm <name>` (dev data is throwaway), rerun |
| `DLL is locked by .NET Host` on rebuild | A previous API process is still running — `Stop-Process` it first |
| Empty admin/usage data | Token usage only appears after at least one chat turn |
| Port already in use | Change `--urls` (Mode B) or stop the stale process |
