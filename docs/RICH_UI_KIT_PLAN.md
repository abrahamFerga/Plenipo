# Rich UI kit & passkey auth — driven by Networthy's v2 plan

Directive (2026-07-11): Networthy's `PLAN.md` v2 (epics 8–14, see
[abrahamFerga/networthy](https://github.com/abrahamFerga/networthy)) planned a home dashboard,
richer visualization, mobile-responsive tables, risk-tiered AI approvals, and two-factor auth,
then flagged an open question: does any of that need platform work, or is it all product-side?
This doc is the answer, verified against the actual `frontend/cortex-ui` source rather than
assumed — and the plan for the pieces that turned out to be genuine platform gaps. Every
generic primitive here is written once and inherited by every product on Cortex (Networthy
today; Casewell and whoever's next tomorrow), same as every prior wave.

Loop protocol: one phase per pass — implement, build, test, commit, mark the checkbox, yield.

## Research notes

Checked against the real component inventory in `frontend/cortex-ui/src/components`, not the
consuming product's assumptions:

- **The "bespoke dashboard" question is already answered — no new seam needed.**
  `BUILDING_A_PRODUCT.md` seam #7 (`<CortexApp moduleUi={[…]} />`) and `ModuleTabView`'s
  `component` prop already let a product register custom React per tab, falling back to the
  generic `GenericTab` otherwise. A product-specific Home/Overview screen composing whatever
  cards that product wants is a **product-side** decision (does it adopt the checkout-based
  frontend build instead of the prebuilt-zip path?) — not a platform gap. Nothing to build here;
  this note exists so the next product asking the same question doesn't re-derive it.
- **The notification inbox already exists end to end** — `NotificationBell.tsx`, the
  `INotifier`/`INotificationChannel` seam, `/api/notifications`, unread badge, mark-read,
  per-category preferences, and `NotificationInfo.link` for deep-linking. A product wanting its
  own domain events (a bill coming due, a budget going over) in that inbox just calls the
  existing seam from its own background work — zero platform changes. Also not a gap.
- **`TabChart` is genuinely line-only.** One y-scale, one geometry (a path per series). No
  proportional (donut/pie) or categorical (bar) mode exists — confirmed by reading the full
  component, not inferred from a changelog.
- **`GenericTab`'s table has no responsive fallback.** Two `<table>` elements, no breakpoint
  logic, no card-mode. Confirmed absent (grepped the file for mobile/breakpoint patterns — zero
  matches).
- **`PendingApprovals` is flat and uniform** — every pending tool call, regardless of stakes,
  renders as the same amber card with a raw tool name and `key: value` argument dump. No
  diff/reasoning rendering, no risk tiering. This is a shared platform component (every product's
  approval queue runs through it), so improving it benefits everyone, not just Networthy.
- **No masked-value rendering anywhere.** Neither `GenericTab`'s cell rendering nor `FieldInput`
  has a masked/reveal-toggle mode, despite `[Pii]` already being a first-class attribute the
  guardrails require every product to tag.
- **No 2FA/passkey/WebAuthn/TOTP exists anywhere in `src`.** Confirmed by a full-repo grep.
  Squarely a platform auth concern per `AddCortexAuthentication`'s existing shape — every product
  inherits it for free once it lands here, the same reasoning that put OIDC and RBAC in the
  platform instead of each product re-deriving them.
- **No personal-access-token / API-key issuance exists.** `AuthorizationSourceOptions`'s
  `Database`/`Token` modes govern how an already-issued bearer token's claims map to
  permissions — a different concern from a product letting its own users self-issue a scoped
  key. Worth a platform seam eventually (any self-hosted product's power users will want this,
  not just Networthy's), but speculative until a second product asks — parked as a later-phase,
  new-ideas-backlog item rather than committed work.

## Phases

- [x] **Phase 0 — this plan** (research + document).
- [ ] **Phase 1 — Chart kinds: proportional + categorical**: extend `TabChartSpec` with a
  `kind` discriminator (`"line"` default, preserving every existing chart unchanged) plus
  `"donut"` (capped segments + an "Other" rollup, matching `TabChart`'s existing categorical
  palette) and `"bar"` (grouped/stacked, for income-vs-expense-style two-series comparisons).
  Same dependency-free SVG approach as the existing line chart — no charting library. Unit
  tests mirror `TabChart.test.tsx`'s structure per kind.
- [ ] **Phase 2 — Stat-tile & progress-bar primitives**: two small, composable, exported
  components — `StatTile` (a labeled number, optional trend sparkline, optional icon) and
  `ProgressBar` (value/target, semantic color banding). Redundant status coding from day one:
  color is never the only signal — pair with an icon and text (e.g. "Over budget by $40", not a
  bare red bar), theme-aware CSS variables for the semantic bands (healthy/warning/critical) so
  light and dark stay legible. These are the pieces a product's own dashboard (via the moduleUi
  seam) or `GenericTab` composes from — not a dashboard layout itself, which stays product-side
  per the research note above.
- [ ] **Phase 3 — `GenericTab` mobile card-mode**: below a breakpoint, each row renders as a
  card (the row's designated title field as the card header, a few key fields visible, the rest
  revealed on tap) instead of a horizontally-scrolling table. Ships in the one shared component
  so every product's tabs gain this for free, not just the ones that ask.
- [ ] **Phase 4 — Masked-value rendering**: a `masked` hint on a `TabDescriptor` field (reusing
  the existing `[Pii]` intent, not inventing a parallel flag) renders as `••••1234` in
  `GenericTab` with a per-field, per-session reveal-on-click toggle; `FieldInput` gains the same
  display mode for form contexts. No new entity, no server change beyond exposing the hint —
  the underlying value was already flowing to the client.
- [ ] **Phase 5 — Risk-tiered, explainable `PendingApprovals`**: a module-declared risk hint on
  `ModuleTool` (`ApprovalRisk: low | high`, defaulting to `high` so nothing silently downgrades)
  changes rendering — low-risk items collapse to a compact one-tap-confirm row, high-risk items
  keep today's fuller card plus two additions: an optional module-supplied `reasoning` string
  ("based on 12 past transactions matching this merchant") and a diff view when the tool call
  carries before/after values. Every AI-originated record already visible in the audit log gains
  a small "AI" badge with a link to its audit entry. Extend the existing audit-log query/export
  with an explicit disclosure view (what was auto-suggested, what a human changed, what a human
  approved as-is) — the concrete ask behind Networthy's "ADMT-readiness" differentiator, but
  generically useful to any product operating under an automated-decision-disclosure regime.
- [ ] **Phase 6 — Two-factor / passkey authentication**: TOTP enrollment + login step and
  WebAuthn/passkey registration + login, recovery codes for lost-factor recovery, admin-assisted
  reset for a locked-out user. Lands in `AddCortexAuthentication`, inherited by every product
  the same way OIDC already is — no product-specific auth code.
- [ ] **Phase 7 — New ideas backlog** (grow as they land):
  - [ ] **Self-service personal access tokens**: a platform `ApiKey` entity (hashed at rest,
    scopes, `LastUsedAt`, revoke), a token-based `AuthenticationHandler` alongside the existing
    dev-auth/OIDC handlers, and — the part that actually matters — confirmation that a
    token-authenticated write still enqueues through the same approval-gate pipeline a chat-tool
    write does; a token is a caller identity, never a bypass. Parked here (not committed to a
    numbered phase) until a second product besides Networthy wants it, per the guardrail against
    building for a problem only one consumer has stated.
