# Getting started with Cortex

Cortex is a **base platform for AI-first, chat-first apps**. This guide gets you from a clone to a
running chat assistant — with three demo verticals (Finance, Nutrition, Legal), an admin/security
dashboard, and token-usage monitoring — in a few minutes. No AI API key required: a built-in **Mock**
provider answers so the chat works out of the box, and it even performs **real, audited tool calls**
(and triggers the human-in-the-loop approval gate) so you can see Cortex's security pipeline with zero setup.

## Prerequisites

- **.NET 10 SDK** — `dotnet --version` ≥ 10
- **Docker** (Desktop) — for Postgres + Redis
- **Node 20+** with **pnpm** — for the React UIs. The frontend is a pnpm workspace; enable pnpm with
  `corepack enable` (ships with Node) so the `pnpm` commands below resolve.
  > **Windows:** `corepack enable` writes to `C:\Program Files\nodejs` and fails with `EPERM` in a
  > non-admin shell. Either run it once from an elevated terminal, or install pnpm per-user with
  > `npm install -g pnpm` — both work.

## Quickstart (3 terminals)

### 1. Start the dependencies

```bash
docker compose up -d        # Postgres on :5432, Redis on :6379
```

### 2. Run the API on port 8080

```bash
dotnet run --project samples/Cortex.Sample.Host
```

This is the sample app built **on** the platform: it installs the Finance, Nutrition, and Legal
modules. On first run it applies the database migrations and seeds a `dev` tenant. The dev URL
(`http://localhost:8080`), connection strings, and the Mock AI provider are all preconfigured
(`launchSettings.json` + `appsettings.Development.json`), so it runs with no flags.

### 3. Run the UI

The frontend is **two apps**: the end-user **domain UI** (`@abrahamferga/cortex-ui`) and the **admin console**
(`@cortex/admin-ui`). Both are Vite dev servers pointed at the API.

```bash
cd frontend
pnpm install
pnpm dev                    # domain UI    → http://localhost:5173
pnpm dev:admin              # admin console → http://localhost:5174/admin   (in a second terminal)
```

Both default to the API at `http://localhost:8080` (override with `VITE_API_BASE` — see `.env.example`).
Running the two dev servers on separate ports, the cross-navigation links don't line up by default; to make
**Admin ↗** (domain UI) and **← Workspace** (admin console) point at each other, set `VITE_ADMIN_URL=http://localhost:5174/admin`
and `VITE_WORKSPACE_URL=http://localhost:5173` (see each app's `.env.example`). The one-command Aspire flow
below wires these automatically.

Open **http://localhost:5173** (the domain UI) and you can:

- **Chat** with each module's assistant — the empty chat offers one-click **starter prompts** (e.g.
  *Summarize my spending*) that immediately exercise the module's tools, so you don't have to guess what to type.
- Switch modules (Finance / Nutrition / Legal) and see server-driven tabs — Finance ships with a sample
  ledger, so its **Transactions** and **Budgets** tabs are populated out of the box.
- Click **Admin ↗** (you're the seeded `system_admin`) to open the **admin console** at
  **http://localhost:5174/admin**: a schema-driven **Roles** editor with the live permission map as an
  in-page reference (toggle what each role grants — try giving `user` the `chat.approvals.manage`
  permission and watch the approvals gate open for that role), **Users**, per-tenant **Modules** and
  **Integrations** (connectors), **Tenants**, **AI Settings** (switch provider/model at runtime, BYO
  API key), **Agent Profiles** (per-agent instructions, tools, and model), **Token Usage**, the
  **Audit Log**, and **Operations**. (In an integrated host the console is served by the API itself
  at `/admin` — see [ARCHITECTURE.md](ARCHITECTURE.md#frontend).)

### See the security pipeline — no API key needed

The Mock provider doesn't just chat; on request it drives **real** tool calls through the same pipeline a
real model would, so you can witness Cortex's signature capability immediately:

1. In **Finance**, send: *"Summarize my spending using a tool."* The assistant actually calls the
   `summarize_spending` tool over the demo ledger, so it comes back with real category totals (try
   *"Am I over budget on anything?"* too — Dining and Entertainment are). In the **admin console**
   (**Admin ↗**, at :5174/admin), open **Audit Log** — the invocation is recorded (who, which tool, the
   permission it required, success, duration).
2. Now send: *"Record a transaction for me."* Recording money is a **side-effecting** tool, so the agent
   is **blocked pending your approval** — it never auto-runs. Approve or reject it from the **Approvals**
   panel. That's the human-in-the-loop gate, working with zero configuration.
3. In the admin console, open **Roles** and expand the **Permission reference** to see *why*: every tool maps to a permission, and the model
   only ever receives the tools the signed-in user is allowed to call (filtered **before** the request is built).

> No identity provider is needed in development — the UI authenticates with `X-Dev-*` headers as a
> `system_admin` in the `dev` tenant. In production this is Entra External ID (see `infra/`).

## Alternative: one command with Aspire (full stack, UIs included)

The Aspire AppHost provisions Postgres + Redis, runs the API, **and launches both front-ends** (the
domain UI and the admin console) as Vite dev servers — the whole stack in one command, with a live
telemetry dashboard. Install the front-end deps once, then run the host:

```bash
dotnet run --project samples/Cortex.Sample.AppHost   # or `aspire run` from that directory
```

pnpm must be on PATH (`corepack enable`, or `npm i -g pnpm` on Windows without admin); the front-end
dependencies install themselves — each UI has an `…-installer` helper resource that runs
`pnpm install` before the dev server starts (a ~1s no-op when deps are already current).

> **Reading the dashboard:** `cortex-ui-installer`, `cortex-admin-ui-installer`, and
> `cortex-sample-rebuilder` are **on-demand helpers, not services** — the installers run to
> completion ("Finished") on every start, and the rebuilder stays "NotStarted" until you use the
> dashboard's Rebuild command. Every real service (API, both UIs, Postgres, Redis, pgAdmin) should
> show **Running / Healthy**.

The dashboard lists every resource with its (dynamic) URL — `cortex-sample` (API), `cortex-ui` (domain
UI), `cortex-admin-ui` (admin console) — and shows logs, traces (including **agent runs and LLM calls**),
and metrics. Aspire wires each UI's `VITE_API_BASE` to the API endpoint and adds the UI origins to the
API's CORS policy automatically, so nothing needs pointing by hand — just open `cortex-ui` from the dashboard.

To use a commercial model, open **Admin → AI Settings** after startup and configure the tenant's
provider, model, and write-only key. The model selector loads the current catalog from the provider.

## Try a real model

The chat uses the **Mock** provider by default. Configure a real LLM per tenant under
**Admin → AI Settings**; commercial credentials are stored only through the secret vault.

With a real model the assistant *reasons* over the tools and decides when to call them (the Mock calls
them on request). The security pipeline is identical either way — permission-filtering before the model
call, audit on every invocation, and the human-in-the-loop approval gate for side-effecting tools.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| **`Cortex could not reach PostgreSQL …`** on startup | Docker isn't running, or the database container hasn't started yet. Run `docker compose up -d`, wait a few seconds, then start the app again. |
| **Port already in use** | The dev ports are **5432** (Postgres), **6379** (Redis), **8080** (API), **5173** (domain UI), **5174** (admin console). If you already run Postgres locally on 5432, stop it — or edit `docker-compose.yml` and the connection strings. |
| **`dotnet test samples/…` won't start** | The integration tests spin up a throwaway Postgres via Testcontainers, so **Docker must be running**. The `Cortex.slnx` unit tests don't need it. |
| **The UI can't reach the API** | The API isn't running, or isn't at `http://localhost:8080`. Start it (`dotnet run --project samples/Cortex.Sample.Host`), or point the UI elsewhere with `VITE_API_BASE` (see `.env.example`). |
| **Chat errors about the AI provider** | Check the tenant connection under Admin → AI Settings. OpenAI/Anthropic require a vaulted tenant key; Azure requires a deployment name/endpoint; the default **Mock** provider needs no setup. |
| **Aspire AppHost exits: `pnpm was not found on PATH`** | The front-end resources are launched via pnpm. Run `corepack enable` (elevated on Windows) or `npm install -g pnpm`, then `pnpm --dir frontend install`, and start the AppHost again. |
| **Aspire stack hangs: containers run but the API never starts (console shows nothing)** | Almost always a **Postgres password/volume mismatch**: Postgres bakes the password into the data volume at first init and never re-reads it, so if the password changed since, every health check fails (dashboard → `cortex-pg` shows `28P01 password authentication failed`) and `WaitFor` blocks the API — and everything downstream — forever. The sample now pins a **stable dev password** (`cortex-dev-only`, overridable via the `Parameters:cortex-pg-password` user-secret), so this only recurs if you change it. To keep your dev data, reset the role inside the running container: temporarily allow local trust in `pg_hba.conf`, `ALTER USER postgres PASSWORD '<the configured password>'`, then restore it. Or just throw the dev data away: `docker volume ls \| findstr cortex`, then `docker volume rm <name>` and rerun. |

## What's next

- **Understand how it works**: [ARCHITECTURE.md](ARCHITECTURE.md) maps the platform, the chat security
  spine, the module system, and the data model (with diagrams).
- **Build your own vertical**: [BUILDING_A_MODULE.md](BUILDING_A_MODULE.md) walks you through a complete
  module from scratch (worked example: `samples/Cortex.Modules.Tasks`). In short — implement `IModule` and
  call `builder.AddCortexModule<YourModule>()`; the dashboard, RBAC, audit, token tracking, and chat all
  apply automatically.
- **Consume Cortex as packages** (NuGet for the backend; `@abrahamferga/cortex-ui` for the domain UI and
  `@cortex/admin-ui` for the admin console) — see [README.md](README.md).
- **Deploy to Azure**: Terraform (Container Apps, Postgres, Redis, Key Vault, Entra External ID) +
  GitHub Actions are in `infra/` and `.github/workflows/`.

## Verify your setup

```bash
dotnet build Cortex.slnx && dotnet test Cortex.slnx
dotnet build samples/Cortex.Samples.slnx && dotnet test samples/Cortex.Samples.slnx   # needs Docker
cd frontend && pnpm install && pnpm -r lint && pnpm --filter @abrahamferga/cortex-ui test && pnpm build:all
```

Those check throwaway instances the tests spin up themselves. To verify the instance **you** just
started, smoke-test it end-to-end (the automated form of the security-pipeline tour above):

```bash
bash eng/smoke-test.sh         # defaults to http://localhost:8080; pass a URL to smoke a deployment
```

It asserts the modules load, the ledger is seeded, the agent makes a real audited tool call, and the
approval gate fires — exiting non-zero if anything is off, so it also works as a post-deploy smoke.
