# Testing Plenipo — the base system's own proving ground

Plenipo is the foundation other systems build on, so the base repo must be runnable and testable
**by itself**, without any real vertical, cloud account, or API key. This document is the official
answer to "how do I run it, and how do I test base modules and integrations here, before a product
consumes them".

## The keyless principle

Everything below works with **zero secrets**: the `Mock` chat provider streams deterministic
replies and performs *real, audited tool calls* (including triggering the approval gate), and the
`Mock` embedder is deterministic. That means the full security pipeline — RBAC tool filtering,
audit, approvals, budgets, RAG — is exercisable in CI and on a fresh clone.

## Layer 1 — run it: the sample host

The runnable demo is `samples/Plenipo.Sample.AppHost` (Finance + Nutrition + Legal). It exists
precisely so the base platform has a living, module-loaded system to poke at:

```bash
dotnet run --project samples/Plenipo.Sample.AppHost   # Aspire: Postgres ×2, Redis, API, both UIs
```

The bare `src/Plenipo.AppHost` runs the platform with **no modules** — useful for platform work,
but chat there has nothing to talk to. Details, headless mode, and API smoke commands:
`.claude/skills/run-plenipo/SKILL.md`.

## Layer 2 — unit tests (platform + frontend)

```bash
dotnet test Plenipo.slnx                # Application/Infrastructure/Api/Cli tests
pnpm -C frontend -r test               # plenipo-ui + admin-ui (vitest)
```

Pure-logic seams are deliberately extracted so they're unit-testable: `InstructionComposer`,
`TokenBudget`, `PermissionMatcher`, `PlenipoSettingsFile.Merge`, skill frontmatter parsing, etc.

`tests/Plenipo.Api.Tests` is not a unit-test project despite living here: it hosts the whole
`Plenipo.Api` in-process over EF InMemory — **no Docker** — and drives the real HTTP pipeline
(dev-auth → permission resolution → the admin endpoints' escalation guards) in seconds. It is the right
tier for endpoint-level RBAC and authorization behaviour; reach for Layer 3 when the behaviour depends on
real SQL, transactions, or a restart.

## Layer 3 — integration tests: the real pipeline against real Postgres

`samples/Plenipo.Sample.Host.IntegrationTests` boots the **whole sample host** via
`WebApplicationFactory` against a **Testcontainers pgvector** instance, and drives it through the
public HTTP surface with dev-auth headers. This suite is where a base capability proves it works
*as a product would consume it*: chat over AG-UI, RBAC enforcement (403s), approvals, connectors
(with a loopback peer + fake OAuth/Graph), RAG ingestion/search, jobs, notifications, budgets.

```bash
dotnet test samples/Plenipo.Samples.slnx    # requires Docker (Testcontainers)
```

When you add a platform capability, add its integration test here — a module-shaped consumer is
the test fixture, which is exactly the guarantee downstream verticals need.

Two things only this layer can prove, both worth knowing about:

- **Behaviour that only exists on a relational provider.** The role-storage conversion claims each tenant
  with a conditional `UPDATE` inside a transaction, routed through the provider's execution strategy — the
  in-memory provider has none of that, so `RoleBaselineIntegrationTests` is the only place the real path
  runs. It caught a startup-breaking bug that every unit test passed straight through.
- **A restart over an existing database.** Derive a second `WebApplicationFactory` over the fixture's
  Postgres (see `RedeclaredRoleFactory`) to exercise "the host restarts with different code against the
  data it already wrote".

**Harness limit worth stating:** `ProductionApiFactory` (and `BootstrapIntegrationTests`) can boot the host
in `Production` with a configured authority, but nothing in this repo can mint a token that survives
`ValidateIssuer` against it. So a Production-mode test asserts through the database, and the HTTP half of
the same behaviour is covered under dev auth in `tests/Plenipo.Api.Tests`.

## Layer 4 — golden conversation evals

`samples/Plenipo.Sample.Host.IntegrationTests/Evals/cases/*.json` are **golden conversations**:
each case says "this message to this module, as this role, must (or must not) call these tools /
require approval / contain this text". They run through the real pipeline (Mock provider) and are
the regression net for agent *behaviour* — instructions, tool selection, handoff, approval gating.
Add a case whenever a behaviour matters enough to defend. See [EVALS.md](EVALS.md).

## Layer 5 — packaging proof

CI verifies the "base, not fork" promise itself on every run:

- `eng/verify-packaging.sh` — packs the NuGet family and builds a throwaway module against the
  produced packages.
- `eng/verify-frontend-packaging.sh` — type-checks a consumer against the built `@plenipo/ui`.

## What a downstream vertical inherits

A product repo (e.g. the-lawyer) repeats only the thin top of this pyramid: its own module tests
plus a small integration suite over *its* host. The platform behaviours (security spine, budgets,
audit, sessions) are already defended here, in the base repo — that's why base-level testing is a
requirement, not a convenience.

## Quick matrix

| I changed… | Run |
|------------|-----|
| Platform library code | `dotnet test Plenipo.slnx` |
| Anything touching the chat pipeline, RBAC, connectors, RAG | `dotnet test samples/Plenipo.Samples.slnx` |
| Agent instructions / tools / approval behaviour | the eval cases (part of the samples suite) |
| Frontend | `pnpm -C frontend -r lint && pnpm -C frontend -r test && pnpm -C frontend build:all` |
| Packaging / public surface | `eng/verify-packaging.sh`, `eng/verify-frontend-packaging.sh` |
