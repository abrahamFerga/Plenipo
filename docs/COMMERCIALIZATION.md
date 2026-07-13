# Selling Plenipo products: site, subscriptions, auto-provisioning

The program plan for turning Plenipo verticals (the-lawyer, …) into self-serve commercial
products: a marketing site + per-product docs, subscription tiers, and payment-driven automatic
provisioning — from "card entered" to "tenant live" (or "dedicated Azure environment live") with
no operator in the loop. Builds directly on [SAAS_OPERATIONS.md](SAAS_OPERATIONS.md); researched
stack decisions below (July 2026).

## Stack decisions (researched; revisit only with cause)

| Concern | Decision | Why (short) |
|---|---|---|
| Payments & subscriptions | **Stripe** (Billing + Checkout + Customer Portal + Tax + webhooks) | Only option with first-class .NET SDK (Stripe.net v50+); native LLM-token metering since the Metronome acquisition — our per-tenant token usage becomes the billing meter directly; seats = `quantity`; self-serve up/downgrade via Portal. Trade-off: we are merchant of record (Stripe Tax monitors thresholds; MoR alternatives like Paddle have no .NET SDK and weak metered billing). |
| Site + docs | **Astro + Starlight** (MIT) | One codebase for brand site, pricing pages, and docs; markdown-first so each product's docs stay in its own repo (pulled into the umbrella build by CI); best landing/pricing-page story of the OSS doc stacks. Versioning starts branch-based; adopt `starlight-versions` when it matures. |
| Dedicated-env automation | **GitHub Actions `workflow_dispatch`**, dispatched by the billing worker | One REST call from the backend; reuses the EXISTING Terraform modules + OIDC; per-tenant state key (`tenants/<id>.tfstate`) + per-tenant concurrency group. Azure Deployment Environments is in maintenance mode; HCP Terraform is the graduation path at dozens of dedicated tenants. |
| Webhook handling | **Event inbox + background worker** (ABP-style, on our jobs primitive) | Verify `Stripe-Signature` on the raw body, persist by `event.id`, 200 immediately; worker processes idempotently + transactionally, re-fetches current state from Stripe (never trusts event order), replayable via `events.list`. Resolve the tenant from event **metadata**, never the request host. |

## Tiers → what gets provisioned

| Tier | Stripe shape | Provisioned |
|---|---|---|
| **Solo** (single user) | 1-seat subscription | Own tenant with 1 seat — full isolation from day one, upgrade = raise seats |
| **Team** (multi-user tenant) | Seat-quantity subscription (+ metered AI tokens unless BYO key) | Tenant + seat limit + first admin invite |
| **Dedicated** (standalone tenant) | High-tier subscription (+ infra fee) | Their own Azure environment via the deploy-customer workflow; same packages |

Every tier maps onto the existing isolation ladder — a Solo tenant IS a tenant; Dedicated IS the
Terraform artifacts. AI billing: platform-managed keys meter through the existing per-tenant
token usage (pushed to Stripe as usage events, optional markup); BYO-key tenants skip metering.

## Entitlement state machine (per tenant)

`provisioning → active → past_due (suspended: flag, nothing deleted) → canceled (grace window) →
deprovisioned (destructive, delayed, reversible for N days)`

- Suspension flips the existing tenant `IsActive` flag (already enforced on every request).
- Seat limit is enforced at user creation/JIT provisioning (new check, per-tenant `MaxSeats`).
- Dedicated deprovision = the same workflow with a `destroy` input, only after the grace window.

## The generic contract, adapted per product

Each product is its own system (own host/repo, sometimes talking peer-to-peer). The platform
ships the machinery once; a product declares its offering:

- **Platform (Plenipo packages — new `Plenipo.Commerce`)**: the `/webhooks/stripe` endpoint +
  event inbox; the entitlement store + state machine; the **provisioning orchestrator**
  (tenant + admin + modules + AI settings + seat limit in one transaction — the
  `tenants:provision` endpoint SAAS_OPERATIONS.md names); the dedicated-env dispatcher
  (GitHub API call + run correlation + callback); usage push to Stripe meters.
- **Product (host code, like modules)**: a `ProductOffering` declaration — product id, the
  plans (mapped to Stripe Price ids via config, never hardcoded), which modules each plan
  enables, seat bounds, default budgets, whether Dedicated is offered, and optional
  product-specific provisioning steps (a hook the orchestrator calls, e.g. seed a demo matter).
- **Site**: one umbrella Astro repo — `/` brand, `/pricing` (per-product pricing components fed
  from one pricing JSON mirroring Stripe Prices), `/products/<id>/` landing, `/docs/<id>/`
  pulled from each product repo's `docs/` by CI.

Peer-to-peer products change nothing here: each side is provisioned independently; the
plenipo-peer connector is configured inside the tenants after both exist.

## Purchase flow (target)

1. Pricing page → Stripe Checkout (Session carries `metadata`: product id, plan, and our own
   provision request — org name, slug, admin email).
2. `checkout.session.completed` → inbox → worker: create entitlement row, run the provisioning
   orchestrator (Solo/Team: tenant live in seconds; Dedicated: dispatch workflow, status
   `provisioning` until the callback), email the admin their sign-in link.
3. `customer.subscription.updated` → re-sync seats/plan/modules. `invoice.payment_failed` →
   suspend at `past_due` (Stripe Smart Retries handle dunning). `customer.subscription.deleted`
   → grace, then deprovision.
4. Customer Portal handles self-serve plan/seat changes and cancellation — we only ever react
   to webhooks.

## Phased roadmap (loop-sized chunks)

1. ~~Research + this plan~~ (done).
2. **Provisioning orchestrator** in the platform: `POST /api/admin/tenants/provision` — one
   transaction: tenant + admin user (subject/email) + role + module enablement + AI settings
   (metered budget or BYOK placeholder) + `MaxSeats`; integration-tested. No Stripe yet — the
   endpoint is the webhook's target and is independently useful to operators today.
3. **Seat enforcement**: per-tenant `MaxSeats` checked at user creation/JIT provisioning.
4. **Plenipo.Commerce**: Stripe webhook endpoint + event inbox + worker on the jobs primitive;
   entitlement store + state machine; `ProductOffering` declaration surface. Keyless tests via
   recorded Stripe events + signature verification with a test secret.
5. **Suspension/deprovision paths**: `past_due` flips `IsActive`; cancellation schedules
   deprovision (grace window) via the jobs primitive.
6. **deploy-customer workflow**: `workflow_dispatch` (tenant id, region, size, apply|destroy),
   per-tenant state + concurrency, callback to mark active. Gated on `AZURE_DEPLOY_ENABLED`.
7. **Usage → Stripe meters**: push per-tenant token usage as Stripe usage events (platform-key
   tenants only).
8. **Umbrella site**: new `plenipo-site` repo (Astro + Starlight): brand, pricing (JSON-driven),
   the-lawyer product page, docs pulled from repos' `docs/`.
9. **the-lawyer end-to-end**: declare its ProductOffering, test-mode Stripe checkout →
   provisioned tenant, live walkthrough.

Constraints that hold throughout: secrets via user-secrets/Key Vault (Stripe keys included,
never committed); all deps MIT-compatible; keyless Mock E2E keeps working (Stripe layer mocks
behind an interface + recorded events); tenant-owned AI connections never fall back to platform
keys; nothing pushes or merges without review.
