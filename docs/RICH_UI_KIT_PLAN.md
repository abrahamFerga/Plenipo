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
- [x] **Phase 1 — Chart kinds: proportional + categorical**: extend `TabChartSpec` with a
  `kind` discriminator (`"line"` default, preserving every existing chart unchanged) plus
  `"donut"` (capped segments + an "Other" rollup, matching `TabChart`'s existing categorical
  palette) and `"bar"` (grouped/stacked, for income-vs-expense-style two-series comparisons).
  Same dependency-free SVG approach as the existing line chart — no charting library. Unit
  tests mirror `TabChart.test.tsx`'s structure per kind. Delivered: `TabChartKind` enum on the
  SDK record (wire = lowercase string literal, mapped explicitly so enum renumbering can never
  shift the contract), the shared palette extracted to `lib/chartTheme.ts`, `TabChartView` as a
  one-fetch dispatcher, `TabDonutChart` (stroke-dasharray segments — handles the 100% single
  slice; direct-labeled legend with value + share; total in the hole) and `TabBarChart`
  (grouped bars in row order, zero always in the domain, negatives hang below an emphasized
  zero line, category-band hover tooltip). Bars were built grouped, not stacked — the named
  driving case (income vs. expense) compares magnitudes, which stacking obscures.
- [x] **Phase 2 — Stat-tile & progress-bar primitives**: two small, composable, exported
  components — `StatTile` (a labeled number, optional trend sparkline, optional icon) and
  `ProgressBar` (value/target, semantic color banding). Redundant status coding from day one:
  color is never the only signal — pair with an icon and text (e.g. "Over budget by $40", not a
  bare red bar), theme-aware semantic bands (healthy/warning/critical) so light and dark stay
  legible. These are the pieces a product's own dashboard (via the moduleUi seam) or
  `GenericTab` composes from — not a dashboard layout itself, which stays product-side per the
  research note above. Delivered: both exported from the package entry with their prop types;
  numbers format through `chartTheme.formatY` so tiles and charts never disagree; `ProgressBar`
  bands at `warnAt` (default 0.85) → warning → critical past target, track capped at 100% with
  the overage stated as text, each band carrying a distinct stroke-drawn icon + status text +
  full `role="progressbar"` semantics. One deliberate deviation from the phase text: band colors
  are static emerald/amber/red Tailwind classes with `dark:` variants (the idiom
  `PendingApprovals` and the chart palette already use), not new CSS variables — status
  semantics are universal, unlike the brand accent, so rebrandability would be a bug.
- [x] **Phase 3 — `GenericTab` mobile card-mode**: below a breakpoint, each row renders as a
  card (the row's designated title field as the card header, a few key fields visible, the rest
  revealed on tap) instead of a horizontally-scrolling table. Ships in the one shared component
  so every product's tabs gain this for free, not just the ones that ask. Delivered: a
  `useMediaQuery` hook (matchMedia-backed, non-matching where absent — jsdom/SSR fall back to
  the wide layout) switches at Tailwind's `md`, the same threshold the sidebar drawer uses;
  first column = card title, next two visible, the rest behind a native `details` disclosure;
  row affordances ride along via a shared `RowButtons` extraction. One layout in the DOM at a
  time — no duplicate content for screen readers, no double fetch.
- [x] **Phase 4 — Masked-value rendering**: a `Masked` hint on `TabColumn` and `TabEditorField`
  (the display-side companion of the `[Pii]` intent) renders as `••••1234` in `GenericTab`
  (table and both card layouts, via one shared `CellValue`) behind a per-cell, per-mount reveal
  toggle; short values mask fully; `FieldInput` types password-style with a Show/Hide toggle.
  No new entity — the underlying value was already flowing to an authorized caller; masking is
  screen privacy, not access control.
- [x] **Phase 5 — Risk-tiered, explainable `PendingApprovals`** *(risk tiers + explanations
  shipped; the disclosure view moved to Phase 7 — see below)*: `ModuleTool.Risk`
  (`ApprovalRisk`, `High` default so nothing silently downgrades), resolved from the declaring
  tool at read time — the declaration stays the living source of truth, no data migration, and
  an unresolvable tool fails safe to full ceremony. Low-risk items collapse to a compact
  one-tap-confirm row; high-risk items keep the full card plus two conventions read from the
  recorded arguments: `reasoning` (the agent's stated why, rendered as prose) and a
  `before`/`after` object pair (rendered as a field-by-field diff). Conventions cost a module
  nothing to adopt: declare the parameter, the agent fills it, the card explains itself.
- [x] **Phase 6 — MFA enforcement (the honest version of "2FA/passkey")**: the original phase
  text assumed a platform credential store; verifying `AddCortexAuthentication` showed there is
  none — production auth is JWT bearer from an external IdP (Entra External ID; Keycloak et al.
  for self-hosters), dev is header-based, anything else fails fast at startup. TOTP/passkey
  *enrollment* therefore belongs to the IdP, which supports it natively, and building a parallel
  WebAuthn stack here would be a second auth system — an anti-feature. What the platform *can*
  own is delivered instead: `Auth:RequireMfa` rejects any validated token whose `amr` claim
  carries no accepted MFA marker (`Auth:MfaAmrValues`, defaults spanning Entra's mfa/ngcmfa,
  fido, otp, hwk; both amr wire shapes handled), so an IdP misconfiguration can't silently admit
  single-factor sessions. Pure, unit-tested judgment (`MfaEnforcement`) + a SECURITY.md
  hardening note. Products wanting "2FA" turn it on at their IdP and set the flag.
- [ ] **Phase 7 — New ideas backlog** (grow as they land):
  - [x] **Mobile bottom-navigation shell mode** (the platform half of Networthy's
    "mobile-responsive navigation" ask — its 13-tab finance module made the flat drawer list poor
    mobile UX): below `md`, primary navigation becomes a fixed bottom bar; the drawer stays as the
    overflow surface. Delivered: `BottomNav` (exported), self-gated on the shared `NARROW_QUERY`
    (same seam as Phase 3's card-mode, so desktop DOM is untouched and jsdom pins the bar's
    absence) with `md:hidden` as the CSS backstop; first four destinations of the same tabs array
    the sidebar renders (Chat first) + a fixed More button reusing `AppShell`'s existing
    `sidebarOpen` drawer — all five show and More drops out at ≤5 tabs; icon-above-label items
    (≥44px targets, safe-area padded, `aria-current` + weight + indicator so active is never
    color-only) resolved by a new single icon map (`TabIcon.tsx`, lucide-style manifest names with
    a neutral fallback — nothing consumed `ModuleTab.icon` before, so this created the mechanism
    rather than forking one); the top-bar hamburger is gone (More is the drawer's one entrance;
    at `md+` it was always CSS-hidden, so desktop is pixel-identical).
  - [ ] **ADMT disclosure view over the audit log** (moved from Phase 5's tail): extend the
    existing audit-log query/export with an explicit automated-decision view — what was
    AI-suggested, what a human changed, what a human approved as-is — plus an "AI" badge on
    AI-originated records linking to their audit entry. The concrete ask behind Networthy's
    ADMT-readiness differentiator, generically useful to any product under an
    automated-decision-disclosure regime.
  - [ ] **Self-service personal access tokens**: a platform `ApiKey` entity (hashed at rest,
    scopes, `LastUsedAt`, revoke), a token-based `AuthenticationHandler` alongside the existing
    dev-auth/OIDC handlers, and — the part that actually matters — confirmation that a
    token-authenticated write still enqueues through the same approval-gate pipeline a chat-tool
    write does; a token is a caller identity, never a bypass. Parked here (not committed to a
    numbered phase) until a second product besides Networthy wants it, per the guardrail against
    building for a problem only one consumer has stated.
