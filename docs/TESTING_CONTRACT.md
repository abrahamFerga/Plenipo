# The fleet testing contract

How the Plenipo platform, the `plenipo-agents` harness, and every product built on the platform
implement and run automated testing — so that a release of the platform cannot break a product
without a machine noticing first, and a product cannot ship a change without proving it at runtime.

This document is the **contract**. [TESTING.md](TESTING.md) is how the platform repo itself is
tested today; the `plenipo-runbook` and `install-runbook` skills in `plenipo-agents` are how the
contract is installed into and run inside a product. When they disagree, this document wins and the
other two are stale.

## 1. Why this exists — what actually broke

Between 2026-08-13 and 2026-09-07 the fleet stopped merging. None of it was a domain bug:

| What happened | Root cause | Class of failure |
|---|---|---|
| Platform CI red on every PR from 2026-08-13 to 2026-09-07; nothing merged to `main` in between | Testcontainers 3.10 → SSH.NET 2023.0.0 hit GHSA-q939-rpr3-3284; `TreatWarningsAsErrors` turned NU1903 into a restore failure. The fix (#174) sat green and unmerged for three weeks. | One transitive advisory in a **test-only** dependency froze the fleet |
| hireworthy `main` red for the same advisory (SSH.NET 2024.2.0) | Each product pins its own Testcontainers in its own copy of the fixture | The same fix has to be made N+1 times |
| Consumer conformance red on every platform dependency bump | The platform raised the `Microsoft.Extensions.AI` floor to 10.9.0; networthy pins 10.8.3 directly → NU1605 | A **dependency-floor raise** is a breaking change nobody classifies |
| `consumers_green` red for every `src/**` PR since 2026-07-29 (#140) | `LinkTemplate` shipped to `main`, never to a tag; conformance tests consumers against unreleased `main`, so the consumer cannot act | **52 unreleased commits** since alpha.28, with breaking API changes |
| "Approved write executes as the approver", "approved write not audited", "ManageApprovals bypasses the tool permission", "approval not scoped to its conversation", "absent `X-Dev-Roles` grants `*`" | Found by four products independently (platform #145 #153 #88 #111 #115 #167; casewell#74 #89; networthy#151 #227; auditworthy#23; hireworthy#46 #51). Each wrote a shim and a private test. The platform accepted the requests but adopted none of the acceptance tests. | **Spine invariants have no platform-owned test**, so every product rediscovers them |
| casewell's UIs never start (casewell#81); still on pre-rename `Cortex.*` alpha.14 | No gate measures version lag; the AppHost composition is never booted in CI | **Lag itself is a failure** and nothing detects it |
| A CSP-pinned inline script, a `frontend/**` change, an additive audit-store rewrite | Known blind spots of the gates (#128, #137, the conformance header) | Green means "the registered consumers still compile and pass their own tests", not "safe" |

Two rules follow, and the rest of this document is their consequence:

1. **The platform publishes the tests, the products execute them.** A product must not own a copy of
   the fixture, the AG-UI parser, the eval runner, or any test of a platform invariant. Those ship in
   a versioned package, so a fix reaches every product on upgrade and a platform request's acceptance
   test protects every product, not just the one that filed it.
2. **Nothing is "done" without a check that fails without it.** Every claim carries its ladder level
   (`loop-discipline`: L1 deterministic, L2 rule, L3 field truth, L4 model opinion, L5 human). A
   regression test is seen red before the fix and green after. The verifier is never the thing edited
   to reach green.

## 2. The ladder

Every repo on the fleet runs the same rungs. What differs is which ones the repo *owns*.

| Rung | Name | Proves | Level | Needs | Runs on |
|---|---|---|---|---|---|
| **0** | Static conformance | config coherent, no secrets, platform pin not lagging, guardrail regexes hold, public API surface unchanged or declared | L2 | nothing | every PR |
| **1** | Unit | domain logic, manifest integrity, permission-string agreement | L1 | nothing | every PR |
| **2** | In-process host | endpoint RBAC and escalation guards through the real pipeline over EF InMemory | L1 | nothing | every PR (platform only) |
| **3** | Integration E2E | real host, real pgvector, real migrations, the approval gate, tenant isolation, the AG-UI protocol | L1 + L3 | Docker | every PR |
| **4** | Golden evals — contract | agent routing, gating, protocol on the deterministic Mock provider | L1 | Docker | every PR |
| **5** | Model-quality evals | tool-call accuracy, groundedness, task adherence with a real model, scored by an LLM judge | L4 (trended) | provider key | nightly, never a PR gate |
| **6** | Frontend | units, real-browser E2E with the API mocked, the shipped bundle is current, no CSP violation on load | L1 | Node | every PR |
| **7** | Sweep + smoke | a whole product exercised as a user would; a deployed instance smoke-tested | L3 | Docker / a URL | after merges, after upgrades, after deploys |

Two helpers decide how far to climb:

- **Lowest rung that catches the bug.** Domain math → 1. Manifest ↔ tool-source drift → 1.
  RBAC 403, approval gate, protocol shape → 3 through `AdminClient()`. Migration, query filter,
  cross-tenant leak → 3 with a second tenant. Tool routing, instructions, descriptions, approval flags
  → 4. Answer quality, reasoning → 5. SPA render → 6. "Does it still work?" → 7.
- **`AuthorizedScopeAsync()` never proves security.** It bypasses RBAC and approvals by design. A
  security-shaped assertion goes through the HTTP client, or it is a false green.

## 3. The conformance kit — `Plenipo.Testing`

A new platform package, versioned with the rest of the family and consumed by every product's
`tests/<Product>.IntegrationTests` project. It replaces the files products copy today.

### 3.1 What it contains

| Component | Replaces | Notes |
|---|---|---|
| `PlenipoHostFixture<TProgram>` | every product's `IntegrationFixture.cs` | Testcontainers **pgvector**, dev-auth `ClientFor(role, tenant)`, `EnsureTenantAsync`, `AuthorizedScopeAsync()`, Ryuk handling, connection-string wiring |
| `AgUiRun.Parse(sse)` | the parser inside every product's `EvalCase.cs` | event types, tool calls, custom events, assistant text, `token_usage` |
| `PlenipoGoldenEvals<TProgram>` | `GoldenConversationEvals` / `GoldenEvalTests` / `EvalTests` (three names for one file across four products) | discovers `Evals/cases/*.json`; unknown fields fail loudly; case schema unchanged |
| `PlenipoSpineConformance<TProgram>` | the private spine tests each product wrote after finding a platform bug | the invariant pack in §3.2, parameterised by a `ProductContract` |
| `PlenipoManifestConformance<TProgram>` | `ManifestGuardTests` in hireworthy and auditworthy | every tool in the manifest has a `ModuleTool` and vice versa, same permission string, every write tool `RequiresApproval`, every tab permission in the security catalog |
| `PlenipoTenancyConformance<TProgram>` | the regex in `validate-product` | reflection over the product's `DbContext` model: every entity carrying `TenantId` has a global query filter; plus an HTTP probe that a second tenant sees nothing on every mapped GET |
| `PlenipoAppHostConformance<TAppHost>` | auditworthy's `AppHost.Tests` | `Aspire.Hosting.Testing`: boots the product AppHost, asserts the Postgres image is pgvector, every declared resource reaches Healthy, `/alive` and `/api/platform/modules` answer |
| `PlenipoRedTeamPack` | nothing — new | prompt-injection, harmful-content and sensitive-data probes through the real guardrail pipeline in enforcement mode; deterministic for rule-based controls |
| `buildTransitive/Plenipo.Testing.props` | each product's own Testcontainers pin | pins Testcontainers and its transitive floors, carries `NuGetAuditSuppress` items with an expiry comment — **an advisory is fixed once, on the platform, and reaches every product on upgrade** |

### 3.2 The spine invariant pack

Each invariant names the fleet issue that proved it was missing. When a platform request is accepted,
its acceptance test is added **here**, not to the sample host's suite — that is what makes the next
release safe for every product at once.

| # | Invariant | Origin |
|---|---|---|
| S1 | A tool the caller may not call is absent from the model's tool list and from `/api/platform/me` | existing |
| S2 | A write tool is parked with `approval_required`; the reply does not claim the write happened | existing |
| S3 | Approving executes the tool **once, as the requester**, and audits three rows: proposal, decision with a non-null approver, execution success | #153, #88, casewell#74, networthy#151, auditworthy#23, hireworthy#46 |
| S4 | An approver who lacks the tool's own permission gets 403 and nothing executes | #145, hireworthy#51 |
| S5 | A conversation's pending approvals can be listed alone, so a chat never offers another thread's write | #111 |
| S6 | Rejecting executes nothing and is audited as a rejection | existing |
| S7 | A permission denial is recorded in the auth audit | #115 |
| S8 | An ungated tool that refuses to act is not audited as a success | #121, networthy#184 |
| S9 | Dev-auth: an empty `X-Dev-Roles` yields no roles; an absent one yields `Auth:Dev:RolesWhenAbsent`, which a product sets to empty so a stripped header can never escalate | #167, networthy#227 |
| S10 | A connector tool approved by a human executes as the requester too | casewell#89 |
| S11 | The AG-UI turn streams `RUN_STARTED … CUSTOM(token_usage) … RUN_FINISHED`, no `RUN_ERROR`, and a usage row exists afterwards | existing |
| S12 | Malformed or absent JSON is a 400, never a 500, on every mapped endpoint | #176, networthy#216 |
| S13 | `/alive` and `/health` answer 200 with `ASPNETCORE_ENVIRONMENT=Production` | runbook §6 |
| S14 | A first-touch user is provisioned exactly once under concurrent requests | networthy#215 |
| S15 | The product's pinned CSP `sha256-` for platform inline HTML matches what the platform serves, or the product pins none | conformance header, `announce-release` |

Status: S1–S7, S9, S11–S14 ship in `PlenipoSpineConformance` (S3 also asserts the `ApprovalDecided`
event and the disclosure view's requester and resolver; S4a is the approvals-permission gate on its
own). S10 holds by construction — connector tools release through the same requester scope — and
has no generic test because the kit cannot assume a connector. S8 waits on #121, which is still
`needs-human`. S15 ships with #197: served shells carry a per-request nonce and the pack checks it.

### 3.3 How a product uses it

```csharp
// tests/<Product>.IntegrationTests/Fixture.cs — the only file a product writes for the harness
public sealed class Fixture : PlenipoHostFixture<Program>
{
    public override ProductContract Contract { get; } = new(
        ModuleId:      "finance",
        ReadTool:      "summarize_spending",
        WriteTool:     "record_transaction",   // must be RequiresApproval = true
        ApproverRole:  "household-admin",      // may chat, call both tools, decide approvals
        NarrowRole:    "household-member",     // may chat, must not hold the write tool's permission
        ReadEndpoints: ["/api/finance/transactions", "/api/finance/budgets"]);
}

[CollectionDefinition("api")] public sealed class ApiCollection : ICollectionFixture<Fixture>;

[Collection("api")] public sealed class Spine(Fixture f)    : PlenipoSpineConformance<Program>(f);
[Collection("api")] public sealed class Manifest(Fixture f) : PlenipoManifestConformance<Program>(f);
[Collection("api")] public sealed class Tenancy(Fixture f)  : PlenipoTenancyConformance<Program>(f);
[Collection("api")] public sealed class Evals(Fixture f)    : PlenipoGoldenEvals<Program>(f);
```

Everything the product adds on top — its journeys, its math, its own eval cases — is written against
the same fixture. `dotnet test` runs the kit's invariants and the product's tests as one suite.

### 3.4 Versioning

The kit ships at the platform version. Upgrading `PlenipoVersion` upgrades the invariants, which is
the point: a product that moves to a release that fixed S3 gets the S3 test in the same step, and a
shim it wrote for S3 now fails a **new** guard instead of silently double-applying. The
`PlatformShimGuardTests` convention stays: a test asserting platform *absence* lives in a class of
that name and carries `[Trait("Category", "PlatformShimGuard")]`; conformance excludes it and reports
it separately as "shims this candidate retires".

## 4. What the platform owes

### 4.1 On every pull request

| Gate | Today | Change |
|---|---|---|
| `ci.yml` — build, unit, in-process, integration, evals, image scan, packaging, frontend | exists | unchanged |
| Deterministic PR gates (`pr-gates.mjs`): `Closes #N`, runtime evidence, red-before-green | exists | add the spine paths to `PATH_RULES` and scan additions as well as removals (#137) |
| **Package validation against the last release** | missing | `EnablePackageValidation` + `PackageValidationBaselineVersion` = last tag on every packable project. A removed or changed public member fails the build unless a suppression file names it. This is the L1 breaking-change detector the fleet has been doing by hand |
| **Dependency-floor diff** | missing | conformance job diffs the RC's transitive floors against the last release's and posts the raised ones; a raised floor is classified **breaking** by `announce-release` |
| Consumer conformance | exists, `src/**` only | trigger on `frontend/**` too and run the consumer's frontend rungs (#128); post the retired-shim list as an annotation |
| Frontend | exists | add a CSP check: load the built shell under the platform's real CSP header and assert zero `securitypolicyviolation` events |

### 4.2 On every merge to `main` — the release train

**Every merge to `main` publishes a numbered prerelease** (`0.1.0-alpha.<run>`) of the NuGet family
and the npm packages to the GitHub Packages feed, and runs `announce-release` against
`consumers.json`. A human promotes a chosen build to nuget.org, public npm, and release assets. This
is the change that makes conformance actionable: the build a consumer was tested against is a build
the consumer can install the same hour, and "on `main` but in no tag" stops being a state.

`announce-release` classifies every consumer-visible difference from the diff, not the changelog,
and treats these as breaking whatever the compiler says: removed or changed public members, changed
registration order or lifetime, a newly required config key, a data-altering migration, a changed
JSON shape a product reads, a flipped default, **a raised dependency floor**, **changed inline HTML**.

### 4.3 Always

- The sample host's integration suite remains the platform's own rung 3, and it runs the kit's
  invariant pack against the three sample modules — the platform is the kit's first consumer.
- Rung 5 runs nightly on the sample host with a real provider, see §6.
- `consumers.json` stays honest: a consumer with `conformance: false` is listed with the reason and
  the issue that unblocks it. An empty registry is a red gate.

## 5. What a product owes

### 5.1 Files and projects

| Path | Purpose |
|---|---|
| `RUNBOOK.md` and `.claude/skills/run-<product>/SKILL.md` | how to run and prove it; real names, ports, module id — never placeholders |
| `Directory.Build.props` with a single `PlenipoVersion` | the swap point for conformance and upgrades |
| `tests/<Product>.<Module>.Tests` | rung 1, including the pinned tool-list assertion |
| `tests/<Product>.IntegrationTests` on `Plenipo.Testing` | rungs 3 and 4; the four kit classes from §3.3 plus the product's journeys |
| `tests/<Product>.IntegrationTests/Evals/cases/*.json` | at least: one read routes to its tool, one write requires approval, one narrow role never sees the write tool, one plain chat |
| `<product>.http` | one request per mapped endpoint with dev-auth headers; a test asserts the catalog covers every route |
| `frontend/<product>-ui` | `pnpm test`, `pnpm build`, and the committed bundle must equal the build output |
| `.github/workflows/ci.yml` | the order in §5.2 |

### 5.2 The CI order, and why it is an order

```text
restore  →  audit  →  build (Release, warnings as errors)  →  rung 1  →  rungs 3+4 (Docker)
         →  frontend: install, audit, test, build, bundle-freshness, CSP smoke
```

Audit runs **before** build so a vulnerable package is named, not buried in a restore error. A
suppression is a `NuGetAuditSuppress` item with the advisory URL, a reason, and an expiry date in
the comment; the kit's props carries the platform's suppressions so a product only ever adds its own.

### 5.3 Every pull request

- Body carries `Closes #N`, a `## Runtime evidence` section with the request exercised and the
  output observed, and a `## Regression test` section naming the test seen red before and green
  after. `agent-gates.yml` fails the PR without them; that is by design.
- A change to a frozen assertion — the pinned tool list, an eval case, an RBAC baseline, an
  approval-gate or tenant-isolation test — lands in its own commit and is called out under
  `## Frozen assertions changed`.
- Test lines changed with production lines unchanged is an explanation to give, not a fix to ship.

### 5.4 Upgrading the platform

`/deliver:upgrade-platform`, never a floating version. Re-vendor, bump the one property, sweep the
other four places the version hides (gitignore globs, `package.json`, the upgrade script, the CSP
hash), unwind every `TODO(plenipo#N)` whose request the release closed, and climb the **whole**
ladder. A product more than two releases behind fails rung 0 (`validate-product` version lag) — lag
is the risk, and the check makes it visible.

## 6. Testing the AI, specifically

The platform is an agent harness, so its tests have to grade *behaviour*, not just code. Four
tiers, kept separate on purpose because they answer different questions and fail for different
reasons.

| Tier | Question | Provider | Deterministic | Gate |
|---|---|---|---|---|
| **Contract evals** (rung 4) | did the platform route, gate and stream correctly for this intent and this role? | Mock | yes | every PR |
| **Trajectory assertions** (rung 3) | did the tool calls happen in the right order, with the right arguments, exactly once, as the right identity, and were they audited? | Mock | yes | every PR |
| **Model-quality evals** (rung 5) | with a real model, is the tool call accurate, the answer grounded in the tool result, the task adhered to, the refusal a refusal? | real | no | nightly; fails only on a regression past a threshold against a stored baseline |
| **Red-team pack** | do the guardrails catch injection, harmful content and sensitive data in enforcement mode, across input, tool call, tool result and output? | Mock for rule-based controls; real for model-based | mostly | every PR for rule-based; nightly for model-based |

Design rules for all four:

- **Cases are data.** The same JSON case file drives rungs 4 and 5. Rung 5 adds an optional
  `quality` block, e.g. `{"toolCallAccuracy": 0.8, "groundedness": 0.7, "taskAdherence": 0.8}`,
  scored with `Microsoft.Extensions.AI.Evaluation` (`.Quality` evaluators, `.Reporting` for the trend
  page). No case is written twice.
- **Judge with a different model than the one under test**, and never let the judge's score be the
  only evidence for a PR. L4 is trended, not gated.
- **Assert the trajectory, not only the final text.** "The reply mentions approval" is weaker than
  "`record_transaction` was proposed once, parked, executed once after approval, by the requester,
  with three audit rows".
- **RAG has its own fixture:** a committed three-document corpus with known chunk ids and page
  numbers; a deterministic retrieval test asserts the expected chunks and `p. N` citations on the
  Mock embedder (rung 3), and a nightly groundedness score on a real model (rung 5).
- **Record what a real model did when it surprised you.** A rung 5 failure that exposes a platform
  defect becomes a rung 3 or 4 case, so it is caught deterministically forever after.

## 7. Breaking-change classes and the gate that catches each

| Class | Caught by |
|---|---|
| Removed or changed public member | package validation on the PR (§4.1) |
| Changed DI lifetime or registration order | spine pack S3–S10 running on every consumer in conformance |
| Newly required config key | `PlenipoAppHostConformance` boot in conformance |
| Data-altering migration | the platform's upgrade-path test: boot the previous release, write, boot the candidate on the same database |
| Changed JSON shape a product reads | the product's `<product>.http` catalog test and the `@plenipo/client` type-check in `verify-frontend-packaging.sh` |
| Flipped default | contract eval cases pinning the old behaviour, e.g. `Rag:Reranker` |
| Raised dependency floor | the floor diff in conformance; NU1605 in the consumer build |
| Changed inline HTML behind a CSP hash | no longer a class: served shells carry a per-request nonce (`PlenipoCsp`), products emit `'nonce-…'` instead of a hash; S15 checks it |
| Advisory in a test-only dependency | the kit's props: fixed once |
| A product merely lagging | rung 0 version-lag check |

## 8. What green means

A green PR on the platform means: it builds; the platform's own suites and the samples pass; the
packages pack and a fresh module compiles against them; the public API surface is unchanged or
declared; **every registered consumer builds and passes the kit's invariants and its own tests
against this exact candidate**; the frontend builds, its E2E passes, and the shell loads under CSP.

It does **not** mean the release is safe for a consumer that is not registered, for behaviour no
test pins, or for reasoning quality — that is rung 5, and rung 5 is a trend. Say "conformant",
"contract-tested", or "swept"; say "works" only after rung 7 on the artifact a user would run.

## 9. Roadmap

In order. Each item names its owner and the check that proves it landed.

| # | Item | Owner | Tracked | Done when |
|---|---|---|---|---|
| 1 | Merge the restore unblocker — done: #174 merged 2026-09-07; the kit's Testcontainers ≥ 4.15 bump then retires the pin by pulling the first patched SSH.NET | platform, human merge | #173 | `ci.yml` green on a `src/**` PR |
| 2 | Cut `v0.1.0-alpha.29` from `main` with migration notes for the three breaking changes in the changelog; run `announce-release` | platform, human tag | #189 | every consumer has an upgrade issue |
| 3 | Continuous prerelease on merge to `main` (§4.2) | platform | #190 | a merge produces a numbered package on the feed within 15 minutes |
| 4 | `Plenipo.Testing` v1: fixture, parser, eval runner, manifest and tenancy conformance, the first five spine invariants, the eval-case targets | platform | #191 | the sample host's suite runs on it; `eng/verify-packaging.sh` compiles a consumer against it |
| 5 | Spine pack S3–S15, each seen red on the sample host before its fix where the fix has not shipped yet | platform | #192 | the accepted requests #145 #153 #111 #115 #167 #176 each close naming a test |
| 6 | Package validation with baseline = last tag; suppression file reviewed like code | platform | #193 | a deliberate public-member removal fails the PR until suppressed |
| 7 | Conformance: `frontend/**` trigger, floor diff, retired-shim annotation, `PATH_RULES` for the spine | platform | #194 | #128, #137, #144 closed by tests in `pr-gates.test.mjs` |
| 8 | `install-runbook` writes the §3.3 files instead of copying the fixture; `plenipo-runbook` and `RUNBOOK.md` cite this contract; `validate-product` gains the version-lag and quarantine-age checks; `upgrade-platform` step 7 runs the kit | `plenipo-agents` | plenipo-agents#44 | a fresh `/deliver:scaffold-product` passes the kit's invariants with no copied harness code |
| 9 | Each product: adopt the kit, delete the copied fixture and runner, keep only journeys and domain tests; networthy first as the reference | products | created by `announce-release` when item 4 ships | `PlatformShimGuardTests` is the only platform-shaped test left in the product |
| 10 | Rung 5 nightly on the sample host: `EVAL_PROVIDER_KEY` secret, `Microsoft.Extensions.AI.Evaluation` 10.9, baseline committed, trend page published | platform | #195 | a deliberate instruction regression on the legal module is reported the next morning |
| 11 | Red-team pack and the RAG fixture | platform | #196 | both run in `ci.yml` |
| 12 | Nonce-based CSP for platform inline HTML | platform | #197 | S15 and the hash sweep in `upgrade-platform` become unnecessary and are removed |

Items 1 and 2 are human acts and unblock everything else. Items 3–7 are platform PRs the steward loop
can carry. Item 8 is one `plenipo-agents` PR. Item 9 is one PR per product, produced by
`/deliver:upgrade-platform` once item 4 has shipped.

## 10. Test hygiene that applies everywhere

- **Quarantine, don't delete.** A flaky test gets `[Trait("Category", "Quarantine")]` and an issue
  link, leaves the PR gate, runs nightly, and fails rung 0 if it is still quarantined after 14 days.
- **No sleeps.** Wait on a signal: `/alive`, a row, an event in the SSE stream.
- **Names state the behaviour**: `Approving_a_write_executes_it_as_the_requester`, never `Test1`.
- **Coverage is reported, never gated.** The gate is the red-before-green rule in every PR body.
- **Mutation testing** (Stryker.NET) monthly on `Plenipo.Application` and `Plenipo.Infrastructure`
  only; its report is read, not gated.
- **Keyless by default.** The only secret any rung needs is the rung 5 provider key, held as a
  repository secret on the platform and never in a product.
