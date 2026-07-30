# ADR 0002: Role permissions — deviation storage vs. materialized baseline

## Status

Accepted (2026-07-30). Supersedes the storage model introduced with configurable RBAC: a tenant now stores
only what it *changed* relative to the declared baseline, and the one-shot startup conversion in
`RoleStorageConversion` migrates existing tenants losslessly. Raised by
[plenipo#72](https://github.com/abrahamFerga/Plenipo/issues/72), filed by the `networthy` product.

## Context

A product declares what its roles mean in code:

```csharp
builder.Services.AddPlenipoRole("household-admin", [/* … */, Permissions.ManageApprovals]);
```

`RoleBaseline.Merge` folds those declarations over `RolePermissions.Defaults` to produce the effective
baseline. Until now that baseline was **materialized**: the first time a tenant was seeded,
`DatabaseInitializer.EnsureRolePermissionsSeededAsync` wrote one `role_permissions` row per role per
permission, and from then on returned early — `if (alreadySeeded) return;`, where `alreadySeeded` was *any*
row for the tenant. `RolePermissionResolution` then treated the presence of any row as authoritative for the
whole tenant, so a role with no rows granted nothing.

Three consequences, in increasing severity:

1. **A declaration change could not reach an existing tenant.** A product that added a permission to a
   declared role shipped a change that was inert on every deployment provisioned before it. `networthy` hit
   this with a real lockout: a household's own admin could not clear a pending approval, so every
   approval-gated write parked until a `system_admin` intervened — and the fix did nothing on the deployment
   that had the problem.
2. **A role declared *after* a tenant was seeded granted nothing at all** on that tenant, which is a sharper
   failure than a missing permission: the role appears to exist and confers no authority.
3. **A per-role write could zero every other role.** Because `tenantHasConfiguration` was computed over the
   whole tenant (`configuredByRole.Count > 0`), writing rows for one role of a previously row-less tenant
   made every *other* role resolve to empty — including `tenant_admin`, which holds
   `platform.roles.manage`, the permission gating the console where an admin would fix it.

Nothing in the build, the tests, or the startup logs surfaced any of this. The declaration and the database
simply disagreed.

## Decision drivers

- A product's declaration must be deliverable. Shipping a permission fix cannot require a human action per
  tenant per release.
- A tenant admin's edit must stay authoritative over the product's declaration, permanently — the platform
  documents deliberately emptying a role as supported, and an upgrade must not undo it.
- The upgrade must not change anybody's effective permissions. An unattended migration that silently grants
  is an RBAC violation across every tenant at once.
- Prefer the shape the platform already uses for "a declaration that must reach tenants without a backfill".

## Options considered

### A. Applied-baseline snapshot + reconciler (rejected)

Record, per tenant, the baseline that was last applied; on startup diff it against the current declaration
and insert what is newly declared. This is the obvious fix and it is what the issue anticipated.

Rejected. Two independent problems:

- **It reintroduces the defect it fixes.** Writing rows for *some* roles of a row-less tenant trips the
  tenant-wide `configured` switch and zeroes the rest — consequence 3 above. The reconciler would have to
  materialize every role of every tenant to be safe, which is the model being replaced.
- **It needs a startup write pass** over every tenant: concurrency control, duplicate-key handling on
  contended boot, unaudited automatic grants, unvalidated writes that could re-add an operator-only
  permission, and an `O(tenants × roles)` cost on every start.

### B. Deviation storage (chosen)

Store only what the tenant changed:

```
effective(role) = ( baseline[role] ∖ suppressed[role] ) ∪ granted[role]
```

`role_permissions` narrows in meaning from "the tenant's full set" to "what this tenant granted *beyond* the
baseline" (plus the entirety of a custom role, which has no baseline). A new `role_permission_suppressions`
table carries "a baseline permission this tenant removed". The tenant-wide `configured` switch is deleted;
there is no such thing as a partially-materialized tenant.

This is the house pattern, not a new idea:

- `UserNotificationPreference` — *"No row means the category is on — rows exist only where a user changed the
  default, so modules can add categories without a backfill."*
- `TenantModuleStore` — *"enablement is default-on: only an explicit `IsEnabled = false` row disables a
  module, so an unseeded tenant sees everything."*
- `AgentProfile.ToolNames` — a stored list only ever *narrows* what RBAC already allows.

`role_permissions` was the one entity in this family that materialized the whole declared state per tenant.
That materialization was the bug.

## Consequences

**What it buys, with no reconciler and no startup write:**

| Requirement | How it holds |
|---|---|
| A baseline **addition** reaches every existing tenant | It was never suppressed, so it simply applies. Immediately, on every tenant. |
| An admin **removal** is permanent | A suppression row is explicit and survives every later baseline change. |
| A **newly declared role** grants its baseline | It has no deviations, so it is exactly its baseline. |
| `AddPlenipoRole(..., replace: true)` **narrowing** propagates | The baseline shrinks; deviations are unaffected. Option A would have silently broken this documented feature. |

**Costs and constraints:**

- `RolePermissionResolution.PermissionsForRoles` changes signature (granted, suppressed, baseline). Source-
  breaking for a product that called it directly. No compatibility overload is offered: one that ignored
  suppressions would be a privilege-escalation footgun.
- `DatabaseInitializer.EnsureRolePermissionsSeededAsync` is removed. Nothing replaces it — role rows no
  longer need seeding, which is the point.
- A product reading `role_permissions` directly now sees only *additions*.
- **The upgrade is one-way.** `RoleStorageConversion` runs once per tenant before any request is served and
  is lossless by construction: for a role in both the legacy rows `C` and the baseline `B` it writes
  suppressions `B ∖ C` and keeps grants `C ∖ B`, giving `(B ∖ (B ∖ C)) ∪ (C ∖ B) = C`. But a tenant's grant
  rows afterwards are a *subset* of what the old resolver expects, so **rolling back past this release
  requires a restore**, and the release should be deployed single-instance or with brief downtime — the
  old binary must not read converted data.

  Each tenant is **claimed** by an atomic conditional `UPDATE … WHERE role_permissions_converted_at IS
  NULL` inside its own transaction, and its legacy rows are read only after that claim's row lock is held.
  This is load-bearing, not belt-and-braces: without it a second instance can list a tenant as pending,
  wait while the first converts it, then read the *already converted* rows as if they were legacy —
  computing `withheld` against a set that no longer restates the baseline and suppressing every role's
  entire baseline. That failure raises no exception and logs a successful conversion, so it cannot be left
  to a duplicate-key rescue. The whole unit runs through the provider's execution strategy, which the
  configured Npgsql retrying strategy requires for any user-initiated transaction.

**The one judgement call.** When the conversion meets a role a legacy tenant has *no* rows for while the
baseline declares one, it cannot tell "the product declared this after the tenant was seeded" from "the admin
emptied this role deliberately" — the old schema carried no state to distinguish them, which is the whole
reason this ADR exists. Both resolve to *grants nothing* today, so the conversion **suppresses the whole
baseline**, preserving current behaviour exactly. The contract is one sentence — *the upgrade changes
nobody's permissions* — and it is testable. Guessing the other way would hand permissions to holders of a
role someone deliberately stripped, on upgrade day, across every tenant, unannounced.

The cost is bounded and repairable: `DELETE /api/admin/roles/{role}/suppressions` restores a role to its
declared baseline in one call, and unlike before the divergence is *visible* as rows rather than an invisible
absence. A tenant with a pending approval and no eligible approver now also logs a warning rather than a
debug line — the signal that would have surfaced the reported lockout in hours instead of after an inert fix
shipped.
