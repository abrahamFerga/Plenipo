# Contributing to Plenipo

Thanks for your interest! Plenipo is a **base platform for AI-first, chat-first apps**; most contributions
are either to the platform itself or a new domain **module**. This guide covers how the repo is laid out,
how to build and test, and the conventions to follow.

New to the project? Start with [GETTING_STARTED.md](GETTING_STARTED.md) (run it) and
[ARCHITECTURE.md](ARCHITECTURE.md) (understand it).

## Prerequisites

- **.NET 10 SDK**
- **Docker** (Postgres + Redis for local runs; also required by the integration tests)
- **Node 20+** (for the React UI)

## Repository layout

```
Plenipo.slnx                  # the base platform — publishable packages + the bare host
  src/                       # Core, Modules.Sdk, Application, Infrastructure, AspNetCore,
                             # ServiceDefaults, Api (thin host), AppHost (Aspire)
  tests/                     # Application.Tests, Infrastructure.Tests
samples/Plenipo.Samples.slnx  # example apps built ON the platform
  Plenipo.Modules.{Finance,Nutrition,Legal}  # demo verticals (+ .Tests)
  Plenipo.Sample.Host         # runnable host wiring all three modules
  Plenipo.Sample.AppHost      # Aspire orchestration for the sample
  Plenipo.Sample.Host.IntegrationTests       # end-to-end API tests (WebApplicationFactory + Testcontainers)
frontend/plenipo-ui           # @plenipo/ui — React + Vite library
infra/                       # Terraform (azurerm) + Entra External ID
.github/workflows/           # CI/CD
```

The platform (`src/`) never depends on a sample. Samples reference the platform via ProjectReference for
local dev; in production they'd consume the `Plenipo.*` NuGet packages.

## Build & test

```bash
# Platform
dotnet build Plenipo.slnx
dotnet test  Plenipo.slnx

# Samples (the integration tests start a Postgres container, so Docker must be running)
dotnet build samples/Plenipo.Samples.slnx
dotnet test  samples/Plenipo.Samples.slnx

# Frontend (a pnpm workspace — install once at the root; covers @plenipo/ui + @plenipo/admin-ui)
cd frontend && corepack enable && pnpm install && pnpm -r lint && pnpm -r test && pnpm build:all

# Packaging: pack the platform and build a throwaway module against the packages
# (proves the "consume Plenipo as NuGet packages" path; also runs in CI).
bash eng/verify-packaging.sh

# Same idea for the frontend: pack @plenipo/ui and type-check a fresh consumer against
# its shipped declarations (proves the npm package is usable; also runs in CI).
bash eng/verify-frontend-packaging.sh
```

All of the above must be green before a PR is merged.

## Conventions

- **Central Package Management** — package versions live in `Directory.Packages.props`; `.csproj` files
  reference packages without a `Version`.
- **Warnings are errors** — `TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest-recommended`
  (see `Directory.Build.props` and `.editorconfig`). Keep the build warning-clean.
- **Match the surrounding code** — comment density, naming, and idioms. New types are `sealed` by default;
  prefer records for DTOs.
- **Security first** — endpoints gate on permissions; agent tools declare the permission they require and
  whether they're side-effecting (`RequiresApproval`). Never bypass the tenant query filters or the audit
  trail.
- **Tests** — add unit tests for new logic; for endpoint behaviour, prefer an integration test in
  `Plenipo.Sample.Host.IntegrationTests` (it exercises the full auth + RBAC pipeline).

## Adding a module

A new vertical is a class implementing `IModule`. For a step-by-step walkthrough see
[BUILDING_A_MODULE.md](BUILDING_A_MODULE.md) (worked example: `samples/Plenipo.Modules.Tasks`); for fuller
references see `samples/Plenipo.Modules.Legal` (smallest stateless) or `Plenipo.Modules.Finance` (owns
persistence). In short:

1. Reference `Plenipo.Modules.Sdk` (+ `Application`, `Core`).
2. Implement `IModule`: a `ModuleManifest` (tools, tabs, roles, agent instructions), `RegisterServices`,
   `MapEndpoints` (and optionally `MigrateAsync` / `SeedAsync` if it owns data).
3. Implement `IModuleToolSource` to supply the executable `ModuleTool`s (each `AIFunction` bound to a permission).
4. Install it in a host: `builder.AddPlenipoModule<YourModule>()`.

The dashboard tabs, RBAC, audit, token tracking, HITL approval, and chat all apply automatically. See
[ARCHITECTURE.md](ARCHITECTURE.md) for how the pieces fit.

## Pull requests

- Keep PRs focused; one logical change per PR.
- Describe what changed and how you verified it.
- Ensure builds, tests, and the frontend lint + tests all pass.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
