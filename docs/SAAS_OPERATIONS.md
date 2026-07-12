# Operating Cortex verticals as SaaS

How a domain product built on Cortex (the-lawyer, etc.) is sold and operated: shared SaaS
tenants, per-customer AI keys and metering, and on-demand dedicated deployments. This document
maps the business flow onto mechanisms that exist today and names the gaps that are deliberate
next steps. The end-to-end commercial pipeline (site, Stripe subscriptions, payment-driven
auto-provisioning) is planned in [COMMERCIALIZATION.md](COMMERCIALIZATION.md).

## Two offering shapes

| Shape | What the customer gets | What runs |
|-------|------------------------|-----------|
| **Shared SaaS** | A **tenant** in our multi-tenant deployment | One deployment, row-level isolation (`TenantId` global query filters, enforced by construction) |
| **Dedicated** | Their own infrastructure, provisioned on demand | A per-customer deployment from the same Terraform/compose artifacts |

## Shared SaaS: license → tenant

Buying a license maps to creating a **tenant** plus its first admin. Everything needed exists as
API surface, so the flow is scriptable today and automatable behind a billing webhook later:

1. `POST /api/admin/tenants` (operator-only) — name + slug; the slug is the customer's stable identity.
2. Provision the first user with the `tenant_admin` role (or map their IdP roles — `Auth:PermissionSource=Token`).
3. Enable the licensed modules for the tenant (Admin → Modules).
4. Set the tenant's AI connection and budget (below).

From there the customer self-serves inside their tenant: roles, users, agent profiles (including
per-agent models and tool selections), connectors with their own credentials, notifications.

**Gap (next step):** a single `POST /api/admin/tenants:provision` convenience endpoint (tenant +
admin + modules + AI settings in one transaction) for the billing webhook to call.

## AI keys per customer: ours or theirs

The per-tenant **provider connection** (Admin → AI Settings) supports both commercial models:

- **Platform-managed (metered)**: we set the provider + our API key on the tenant, and cap spend
  with the tenant's **monthly token budget** (admins are alerted at 80% and exhaustion; chat
  refuses beyond it). Usage is recorded per tenant / per **user** / per module / per model, so
  the usage dashboard doubles as the billing meter.
- **Bring-your-own-key**: the customer switches the provider and enters *their* key themselves.
  The key is write-only (vaulted via `ISecretVault` — DataProtection or Key Vault), and a
  tenant-owned connection **never falls back to the deployment's endpoint or key**, so their
  traffic can't silently bill us (tested invariant).

Both take effect on the next chat turn — no restart, no redeploy. Token usage attributes each
turn to the **effective** provider/model (including an agent profile's per-agent model), so mixed
fleets meter correctly.

**Per-user keys** are deliberately not a thing yet: usage *reporting* is already per user, but
keys and budgets are per tenant. If a real customer needs per-seat keys, the extension point is
the same vault + resolver pair, scoped one level deeper.

## Dedicated deployments on demand

The deployment artifacts are already customer-agnostic:

- **Azure**: [infra/](../infra/) Terraform — Container Apps, Flexible Postgres (pgvector), Redis,
  Key Vault, Entra External ID, OIDC for CI. One customer = one `terraform.tfvars` (name prefix,
  region, SKUs, `enable_keyvault_secret_vault`) + one Terraform
  **workspace/state** per customer.
- **Single-box**: [deploy/compose/](../deploy/compose/) — one compose file, pinned tags, all state
  in two named volumes + `.env`; upgrade = `docker compose pull && up -d`. Right-sized for small
  dedicated customers or on-prem.

**Automation shape (next step, once the repo has a CI remote):** a `deploy-customer` GitHub
Actions `workflow_dispatch` pipeline — inputs `customer`, `region`, `sku`; steps: select/create the
Terraform workspace, `terraform apply` with the customer tfvars, run `cortex init
--non-interactive` for host settings, smoke-test `/alive`. Deprovisioning is `terraform destroy`
on the workspace. Nothing in the artifacts blocks this today; it's CI wiring, not platform work.

## Isolation ladder (what to sell when)

1. **Tenant** in shared SaaS — cheapest, instant, row-level isolation, per-tenant budgets/keys.
2. **Dedicated deployment, shared ops** — own database and compute via Terraform; for customers
   with data-residency or noisy-neighbour concerns.
3. **On-prem / their cloud** — the compose bundle or Terraform in *their* subscription; secrets
   never leave them.

The same packages, admin console, and security spine run at every rung — that's the point of the
base platform.
