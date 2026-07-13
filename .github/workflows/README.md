# Cortex — CI/CD Workflows

Three GitHub Actions workflows. All Azure access uses **OIDC federation** — no
`ARM_CLIENT_SECRET` or any cloud password is stored.

| Workflow       | Trigger                                   | Purpose                                              |
| -------------- | ----------------------------------------- | ---------------------------------------------------- |
| `ci.yml`       | PRs, pushes to non-`main` branches        | .NET build/test, Docker image build, Trivy scan, frontend build/lint |
| `deploy.yml`   | push to `main`, `workflow_dispatch`       | Build & push image to ACR, `terraform apply`, smoke test |
| `pr-check.yml` | PRs touching `infra/**`                    | `terraform fmt`/`validate`/`plan` + plan PR comment  |

## Required secrets

Set under **Settings → Secrets and variables → Actions** (repo or environment
scope as noted):

| Name                          | Scope        | Description                                                        |
| ----------------------------- | ------------ | ------------------------------------------------------------------ |
| `AZURE_CLIENT_ID`             | repo/env     | Client ID of the CI/CD user-assigned managed identity (`terraform output cicd_identity_client_id`). |
| `AZURE_TENANT_ID`             | repo/env     | Azure AD tenant ID for the deployment subscription.                |
| `AZURE_SUBSCRIPTION_ID`       | repo/env     | Target Azure subscription ID.                                      |
| `ACR_LOGIN_SERVER`            | repo/env     | ACR login server, e.g. `cortexdevacrx1y2z3.azurecr.io` (`terraform output acr_login_server`). |
| `TF_BACKEND_RESOURCE_GROUP`   | repo         | Resource group holding the Terraform state storage account.        |
| `TF_BACKEND_STORAGE_ACCOUNT`  | repo         | Storage account name for remote state.                             |
| `TF_BACKEND_CONTAINER`        | repo         | Blob container name for state (e.g. `tfstate`).                    |
| `TF_STATE_KEY`                | repo/env     | State blob key per environment (e.g. `cortex-staging.tfstate`).   |

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

`actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-node@v4`,
`azure/login@v2`, `hashicorp/setup-terraform@v3`, `actions/github-script@v7`,
`aquasecurity/trivy-action@0.28.0`.

## Notes / assumptions

- The API Dockerfile is expected at `src/Cortex.Api/Dockerfile` (created
  separately).
- The frontend is a **pnpm workspace** at `frontend/` (packages `@abrahamferga/cortex-ui` and
  `@cortex/admin-ui`); CI runs `pnpm -r lint`, `pnpm -r test`, `pnpm build:all`, and
  the `@abrahamferga/cortex-ui` Playwright E2E.
- `deploy.yml` deploys **staging** on push to `main`; promote to **production**
  via the manual `workflow_dispatch` (which enforces the approval gate).
