# ADR 0001: Pluggable endpoints — DI modules vs. container-per-endpoint

## Status

Accepted — Option A, plus the config-gate half of Option B (2026-07-11): `Modules:Exclude` /
`Connectors:Exclude` let a deployment suppress compiled-in components by configuration alone, and
`AddPlenipoConnectorsFrom(assembly)` registers a whole connector package in one call (the built-in
bundle exposes it as `AddPlenipoConnectors()`). The assembly-directory loading half of Option B —
loading modules a host never referenced at compile time — remains not adopted: no host has needed
it, and its `AssemblyLoadContext` complexity stands.

## Context

Plenipo's module system (`IModule`, see [ARCHITECTURE.md](../../ARCHITECTURE.md#the-module-system)) already
lets a vertical register its own HTTP routes via `MapEndpoints(IEndpointRouteBuilder)` and its own services
via `RegisterServices(IServiceCollection, IConfiguration)`. Today every installed module runs **in the same
process** as the host (`samples/Plenipo.Sample.Host`), registered with `builder.AddPlenipoModule<TModule>()`.
Neither `src/Plenipo.AppHost/AppHost.cs` nor `samples/Plenipo.Sample.AppHost/AppHost.cs` orchestrates module
code as a separate container — there is exactly one API resource per AppHost.

The question raised: should individual API endpoints (or modules) become independently injectable units —
either via a more formal DI-based plugin seam, or by running each as its own Docker container — so that a
host product can compose endpoint sets without recompiling against every module, or scale/isolate modules
independently?

This matters because the platform's core security guarantee — **tool security before the model call** — is
enforced by `AuthorizedAgentRunner` filtering the *in-process* `IModuleToolSource` registrations against the
caller's permissions before the LLM ever sees a tool schema (see `src/Plenipo.Infrastructure/Agents/
AuthorizedAgentRunner.cs`). Any change to how modules are composed has to preserve that pre-model-call
filtering, the dual-database audit trail, and the global tenant query filters — all of which currently rely
on shared `IServiceProvider`/`DbContext` state within one process.

## Decision drivers

- Preserve the pre-model-call tool-filtering security spine without per-module reimplementation.
- Keep the manifest-first contract (`ModuleManifest`) as the single source of truth for tools/tabs/roles, so
  the platform can reason about a module without running its code.
- Don't regress the proven embeddability story (NuGet + npm packages, verified in CI by
  `eng/verify-packaging.sh` / `eng/verify-frontend-packaging.sh`).
- Avoid distributed-systems cost (network hops, partial-failure handling, cross-process auth propagation)
  unless a concrete isolation or scaling need justifies it.

## Options considered

### A — Status quo: in-process DI modules (current)

Modules are plain assemblies installed via `AddPlenipoModule<T>()`. Pluggability is at the **compile/install**
boundary (which modules a host references), not at runtime.

- ✅ Tool filtering, audit, and tenant isolation work unmodified — they already assume one `IServiceProvider`.
- ✅ Zero new infrastructure; matches the existing embedding story.
- ❌ A host must reference and recompile against every module it wants; no late-bound "enable this module via
  config alone" story yet.
- ❌ No fault/resource isolation between modules — a runaway module shares the host's process.

### B — Formalize a runtime DI plugin seam (assembly-load or config-gated modules)

Extend `IModule` discovery so modules can be loaded by convention (e.g., a `modules/` directory of assemblies
scanned at startup, gated by config/feature flag) instead of a hard-coded `AddPlenipoModule<T>()` call per
module, while still running in-process.

- ✅ Keeps everything from Option A's security/audit guarantees (still one process, one `IServiceProvider`).
- ✅ Lets a host enable/disable modules via configuration without a recompile — closer to "inject endpoints
  as DI" in the sense the question intends.
- ❌ Assembly-load plugin systems add real complexity (version conflicts, isolation via `AssemblyLoadContext`,
  startup ordering) for a benefit (config-only enablement) that may not be needed yet — no host has asked for
  it.

### C — Container-per-endpoint / container-per-module (gateway + sidecars)

Each module (or even each endpoint group) runs as its own container behind a gateway that forwards requests
into `AuthorizedAgentRunner`-equivalent logic.

- ✅ True fault and resource isolation; independent scaling and deploys per module.
- ❌ Breaks the pre-model-call tool-filtering spine as designed: filtering would need to happen either in the
  gateway (duplicating permission logic per call) or be re-fetched per container (extra round-trip per tool
  invocation), and the model would no longer get a single coherently-filtered tool list without an aggregation
  step.
- ❌ Audit and tenant-isolation guarantees (global EF Core query filters, dual-database audit) are
  process-local today; container-per-module would require either a shared audit/tenant service called over
  the network from every container, or duplicating that logic per container — both weaken the "no query can
  cross a tenant boundary" guarantee that today holds by construction.
- ❌ Significant new operational surface (service discovery, inter-container auth, partial-failure handling)
  with no current driver (no module has resource or scaling needs distinct from the others).

## Decision

Adopt **Option A today, Option B as the next step if/when a host needs config-only module enablement**.
**Option C is explicitly out of scope** until a module demonstrates a concrete isolation or independent-scaling
need that outweighs re-deriving the security spine across process boundaries — at that point it should be
reconsidered as its own ADR scoped to *that* module, not a platform-wide default.

Endpoints remain registered via `IModule.MapEndpoints` into a single host process per AppHost. "Pluggability"
in Plenipo means *which modules a host installs*, not *which container an endpoint runs in*.

## Consequences

- No new infrastructure required now; the embedding story (NuGet + npm) stays the actual pluggability
  mechanism.
- If config-driven module enablement (Option B) is pursued later, it must continue to flow through
  `IModule.RegisterServices`/`MapEndpoints` against the host's existing `IServiceProvider`, not a new
  per-module container/process.
- A future module with genuine isolation/scaling needs should get a scoped ADR re-evaluating Option C against
  that module's actual requirements, not a platform-wide rearchitecture.
