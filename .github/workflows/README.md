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
| `COPILOT_GITHUB_TOKEN` | secret | Fine-grained user PAT for Copilot inference and loop mutations; requires Copilot Requests read, Actions read, and Contents, Issues and Pull requests write on this repo |
| `AGENT_AUTOMERGE` | variable | Optional kill switch; set to `off` to make the scheduled merger no-op |
| `AGENT_TRIAGE_RECOVERY` | variable | Optional independent kill switch; set to `off` to stop scheduled issue-triage recovery without disabling PR merging |

The mutation path deliberately uses `COPILOT_GITHUB_TOKEN`: GitHub suppresses downstream workflow
events caused by the built-in `GITHUB_TOKEN`, which otherwise strands approval-label reruns, branch
updates and post-merge deploy/publish runs. No token needs Administration access because required
contexts are read through `gh pr checks --required`. Verdict dispatch/rerun is the exception: it
uses the workflow's scoped `GITHUB_TOKEN` with Actions write, while the reviewer explicitly allows
`github-actions[bot]` as its bootstrap actor.

## Bounded triage recovery

`agent-merge.yml` has a separate `triage-recovery` job on the same 15-minute schedule and manual
dispatch as the merger. It is independent of `AGENT_AUTOMERGE`, has no merge permission, and uses
the scoped `GITHUB_TOKEN` only to dispatch or re-run an active triage workflow. Set the repository
variable `AGENT_TRIAGE_RECOVERY=off` to disable this repair path without stopping approved PRs from
merging.

Issue events remain the fast path. A new request runs when its `platform-request` label is applied,
a request with no final verdict runs when reopened, and a request carrying `triage:needs-info` runs
again when the requester edits the issue body. The scheduler repairs missed or incomplete events:
it considers only open target-labeled issues, skips `needs-human`, `human-hold`, `agent:blocked` and
every final `triage:*` verdict, and queues at most two actions per tick. If no run exists under the
current versioned title (`Triage platform request v2 #<issue>`), it dispatches the exact workflow
from the default branch with that `issue_number`. If a completed run produced no final verdict, it
re-runs that same attempt after exponential backoff: 30, 60, 120, 240, then at most 360 minutes.
Before GitHub's 30-day or 50-attempt rerun ceiling can strand it, recovery renews the issue with a
fresh current-policy dispatch. Active runs are never duplicated.

`triage:needs-info` is deliberately non-terminal. After a successful needs-info run, recovery waits
until the issue body was edited after that run began, then starts a fresh targeted dispatch so the
workflow reads the revised request. The requesting product's normal `deliver` or `fleet` tick finds
the request through its `TODO(plenipo#N)` tag, consumes the machine-readable question, and edits the
existing body; harness reports use their equivalent local-note marker. A final verdict removes
`needs-info` and `triage:needs-info`. This body-edit handshake supplies missing evidence without a
human relay or an old run being mistaken for current-policy triage. The requester accepts only a
`github-actions[bot]` comment carrying the exact issue/run marker, then verifies that run's v2 title,
success, event and protected default-branch provenance before treating its question as untrusted
factual input.

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
