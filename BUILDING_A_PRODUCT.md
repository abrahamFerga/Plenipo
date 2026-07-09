# Build a product on Cortex

A **product** is its own system — its own repo, host, deployment, and brand — built from the
Cortex packages. [the-lawyer](https://github.com/abrahamFerga/the-lawyer) is the reference:
the legal domain lives there; the security spine (tenancy, RBAC, approvals, audit, budgets,
channels, billing) comes from the platform. This guide is the catalog of every seam a product
customizes — **no forks**. For writing the domain module itself, start with
[BUILDING_A_MODULE.md](BUILDING_A_MODULE.md).

## The shape of a product host

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddCortex();                                   // the platform

builder.AddCortexModule<LegalModule>();                // 1. your domain
builder.AddCortexConnector<GoogleDriveConnector>();    // 2. data sources you offer

builder.Services.AddCortexProduct(new ProductOffering  // 3. what you sell
{
    ProductId = "the-lawyer",
    Plans =
    [
        new ProductPlan { Id = "solo", Modules = ["legal"], DefaultSeats = 1, MonthlyTokenBudget = 200_000 },
        new ProductPlan { Id = "team", Modules = ["legal"], DefaultSeats = 5, MonthlyTokenBudget = 500_000 },
        new ProductPlan { Id = "dedicated", Dedicated = true },
    ],
});
builder.Services.AddCortexTenantProvisionedHook<WelcomeEmailHook>();   // 4. act on provisioning
builder.Services.AddCortexNotificationChannel<SmsChannel>();           // 5. your delivery channels
builder.Services.AddCortexPlatformTools<OrgContextToolSource>();       // 6. product-wide tools

var app = builder.Build();
app.UseCortex();
app.Run();
```

## The seams

| # | You want to… | The seam | Worked example |
|---|---|---|---|
| 1 | Ship domain behavior: tools, tabs (with editors and drill-downs), agents, workflows, skills | `IModule` + `ModuleManifest` (incl. `Agents`, `Workflows`, `SkillsPath`) | [BUILDING_A_MODULE.md](BUILDING_A_MODULE.md); `samples/Cortex.Modules.Legal` |
| 2 | Offer data sources (service or per-user OAuth) | `IConnector` + `AddCortexConnector<T>()`; delegated OAuth URL shape comes from the manifest's `OAuthAuthorizeUrlTemplate`/`OAuthTokenUrlTemplate` (Entra default, Google-style fixed URLs supported) | `src/Cortex.Connectors/GoogleDrive` |
| 3 | Sell plans that provision themselves | `AddCortexProduct(new ProductOffering { … })` — the PLAN is authoritative for modules/seats/budget/dedicated, never checkout metadata | `samples/Cortex.Sample.Host/Program.cs` |
| 4 | Act right after a tenant is provisioned (welcome email, domain seeding, external registration) | `ITenantProvisionedHook` + `AddCortexTenantProvisionedHook<T>()` — post-commit, best-effort, fired for operator AND billing-webhook provisioning | `samples/Cortex.Sample.Host/WelcomeEmailHook.cs` |
| 5 | Deliver notifications your way (SMS, chat-ops, …) | `INotificationChannel` + `AddCortexNotificationChannel<T>()` — fan-out is best-effort per channel; in-app inbox is always the baseline. Email is built in: configure the `Email:` section | `src/Cortex.Infrastructure/Notifications/EmailNotificationChannel.cs` |
| 6 | Add product-wide agent tools (offered in every module's chat) | `IPlatformToolSource` + `AddCortexPlatformTools<T>()` — same RBAC gate, approval flags, and audit as everything else | `src/Cortex.Infrastructure/Documents/DocumentToolSource.cs` |
| 7 | Brand the workspace UI | `<CortexApp branding={{ name, logo }} moduleUi={[…]} />` — name/logo in the shell, custom React per tab via the module UI registry; everything else stays server-driven | `frontend/cortex-ui/README.md` |
| 8 | Shape identity and roles | `Auth:` config — `PermissionSource` (`Database`/`Token`), `DefaultRole` (what a JIT user gets when their token asserts nothing; empty = permission-less). Declare PRODUCT roles / reshape built-in baselines with `AddCortexRole("paralegal", [...])` — seeded into every tenant, runtime-editable afterwards in Admin → Roles. `system_admin` is never customizable | `samples/Cortex.Sample.Host/Program.cs` |
| 9 | Swap infrastructure pieces | `ISecretVault` (DataProtection/Key Vault), `IOcrEngine`, `IEmbeddingGenerator`, `ISmtpTransport` — one DI registration each | `src/Cortex.Infrastructure` |

## Rules the platform holds for you (don't fight them)

- **Narrowing, never granting**: agent/profile tool selections can only shrink what RBAC
  already allows. There is no seam that bypasses a permission gate — by design.
- **Approval-first writes**: connector and module tools that change state declare
  `RequiresApproval = true` and ride the human-in-the-loop lane.
- **Secrets are write-only**: anything secret (API keys, webhook secrets, SMTP passwords,
  connector client secrets) goes through configuration user-secrets/Key Vault or the vault —
  the platform never echoes a stored secret to any caller, and neither should your product.
- **Tenant isolation by construction**: your entities implement `ITenantOwned` and inherit the
  global query filter; background code sets tenant ids explicitly.
- **Keyless by default**: everything above is testable with the Mock provider, the fake IdP,
  and recording seams — a product's CI needs no external accounts. Copy the patterns in
  `samples/Cortex.Sample.Host.IntegrationTests`.

## Shipping the web UI (no npm registry needed)

The `@cortex/*` frontend packages don't need a registry to reach production. Build them with
your product's identity baked in and drop the outputs into the host — the API serves the SPA
itself, same origin, no CORS, no asset host:

1. `VITE_BRAND_NAME=YourProduct VITE_API_BASE= pnpm -C frontend/cortex-ui build` → copy
   `dist/` to your host's `wwwroot/app` (served at `/`, with an `index.html` fallback for
   client-side deep links; `/api`, `/admin`, `/hubs`, health, and OpenAPI are never shadowed).
2. `pnpm -C frontend/admin-ui build` → copy `dist/` to `wwwroot/admin` (served at `/admin`).

Both mounts are no-ops when the directories are absent, so an API-only host and the dev-time
Vite servers (which the sample AppHosts launch with hot reload) keep working unchanged. See
casewell's `scripts/build-ui.ps1` for a worked one-command version.

## What's deliberately NOT extensible (yet)

- **Admin console pages** — the console is a fixed surface; product-specific admin UI lives in
  your product's own frontend for now.
- **Inbound conversation channels** — WhatsApp is the only inbound lane today. The
  inbound-channel SDK (SMS/Telegram/email-intake adapters, WhatsApp as the first one) is
  designed in [docs/INBOUND_CHANNELS.md](docs/INBOUND_CHANNELS.md). Outbound (notification)
  channels are already open — seam #5.

When one of these blocks you, open an issue rather than forking — the seam list above grew
exactly that way.
