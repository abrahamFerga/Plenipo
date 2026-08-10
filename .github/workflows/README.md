# Plenipo — CI/CD Workflows

The repository has four workflow families. All Azure access uses **OIDC federation** — no
`ARM_CLIENT_SECRET` or cloud password is stored.

| Family | Files | Purpose |
| ------ | ----- | ------- |
| Required PR verification | `ci.yml`, `agent-gates.yml`, `consumer-conformance.yml`, `pr-check.yml` | build/test/package/security checks, deterministic PR policy, consumer compatibility and Terraform planning |
| Autonomous review and merge | `pr-approval-verdict.md` + compiled lock, `agent-approval-reset.yml`, `agent-merge.yml` | independent model verdict, stale-verdict expiry, bounded retry and deterministic squash merge |
| Platform routing | `platform-request-triage.md`, `platform-release-impact.md` + compiled locks | triage product requests and route release impact |
| Publish and deploy | `publish.yml`, `deploy.yml`, `deploy-customer.yml` | publish packages and deploy platform/customer environments |

`ci.yml` runs once per pull request. The approval verdict is the only PR model reviewer in the
unattended profile; a second intent reviewer would duplicate model load without adding a gate.
`agent-merge.yml` considers only GitHub-required checks plus path-scoped conformance/Terraform
checks, requires a live `agent:approved` label and its approval-specific safe-output artifact, and
re-evaluates every gate immediately before merging with an exact-head guard. The reviewer runs in
`pull_request_target` without checkout, so the policy on the protected base judges the PR. A PR that
changes the loop's own controls is also re-evaluated with `pr-gates.mjs` downloaded from the
protected base; it cannot approve itself with the workflow wrapper or reviewer it proposes.

## Agent loop configuration

| Name | Kind | Description |
| ---- | ---- | ----------- |
| `COPILOT_GITHUB_TOKEN` | secret | Fine-grained user PAT for Copilot inference and loop mutations; requires Copilot Requests read plus Actions, Contents, Issues and Pull requests write on this repo |
| `AGENT_AUTOMERGE` | variable | Optional kill switch; set to `off` to make the scheduled merger no-op |

The mutation path deliberately uses `COPILOT_GITHUB_TOKEN`: GitHub suppresses downstream workflow
events caused by the built-in `GITHUB_TOKEN`, which otherwise strands approval-label reruns, branch
updates and post-merge deploy/publish runs. No token needs Administration access because required
contexts are read through `gh pr checks --required`.

## Required secrets

Set under **Settings → Secrets and variables → Actions** (repo or environment
scope as noted):

| Name                          | Scope        | Description                                                        |
| ----------------------------- | ------------ | ------------------------------------------------------------------ |
| `AZURE_CLIENT_ID`             | repo/env     | Client ID of the CI/CD user-assigned managed identity (`terraform output cicd_identity_client_id`). |
| `AZURE_TENANT_ID`             | repo/env     | Azure AD tenant ID for the deployment subscription.                |
| `AZURE_SUBSCRIPTION_ID`       | repo/env     | Target Azure subscription ID.                                      |
| `ACR_LOGIN_SERVER`            | repo/env     | ACR login server, e.g. `plenipodevacrx1y2z3.azurecr.io` (`terraform output acr_login_server`). |
| `TF_BACKEND_RESOURCE_GROUP`   | repo         | Resource group holding the Terraform state storage account.        |
| `TF_BACKEND_STORAGE_ACCOUNT`  | repo         | Storage account name for remote state.                             |
| `TF_BACKEND_CONTAINER`        | repo         | Blob container name for state (e.g. `tfstate`).                    |
| `TF_STATE_KEY`                | repo/env     | State blob key per environment (e.g. `plenipo-staging.tfstate`).   |

> Per-environment values (`TF_STATE_KEY`, possibly `AZURE_*`) are best set as
> **GitHub Environment** secrets on the `staging` / `production` environments so
> each deploy targets the right state and gate.

## GitHub Environments (approval gates)

Create environments under **Settings → Environments**:

- **`staging`** — no required reviewers (auto-deploys on push to `main`).
- **`production`** — add **Required reviewers** so `deploy.yml` pauses for manual
  approval. Selected via `workflow_dispatch` → environment = `production`.
- **`development`** — optional, used by `pr-check.yml`'s dev plan / dev tfvars.

## OIDC federated credentials

The `cicd-identity` Terraform module creates the federated credentials on the
CI/CD managed identity. Trusted subjects (issuer
`https://token.actions.githubusercontent.com`, audience
`api://AzureADTokenExchange`):

| Credential     | Subject claim                                          | Used by                          |
| -------------- | ------------------------------------------------------ | -------------------------------- |
| `gh-main`      | `repo:OWNER/REPO:ref:refs/heads/main`                  | `deploy.yml` (push to main)      |
| `gh-pull-request` | `repo:OWNER/REPO:pull_request`                      | `pr-check.yml`, PR-time logins   |
| `gh-env-production` | `repo:OWNER/REPO:environment:production`          | `deploy.yml` (production gate)   |
| `gh-env-staging` / `gh-env-development` | `repo:OWNER/REPO:environment:<env>` | per-environment deploys |

Replace `OWNER/REPO` with your repo (set `github_owner` / `github_repo` /
`github_environments` in the tfvars). When a workflow runs **inside a GitHub
environment**, the OIDC subject becomes the `environment:` form — so the matching
`gh-env-*` credential must exist for that environment, which is why
`github_environments` should list every environment you deploy to.

## Pinned action versions

Workflow files are the source of truth for action pins. Agentic `.lock.yml` files are generated by
`gh aw` and must be regenerated from their Markdown source rather than edited directly.

## Notes / assumptions

- The API Dockerfile is expected at `src/Plenipo.Api/Dockerfile` (created
  separately).
- The frontend is a **pnpm workspace** at `frontend/` (packages `@plenipo/ui` and
  `@plenipo/admin-ui`); CI runs `pnpm -r lint`, `pnpm -r test`, `pnpm build:all`, and
  the `@plenipo/ui` Playwright E2E.
- `deploy.yml` deploys **staging** on push to `main`; promote to **production**
  via the manual `workflow_dispatch` (which enforces the approval gate).
