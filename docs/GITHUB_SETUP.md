# GitHub project setup

How to stand up the GitHub side of a repo built on this base: repository settings, branch
protection, labels, the Projects v2 board, the deterministic merge gates, CODEOWNERS, secrets, and
the agentic workflows — plus **which skill runs each step**, so you configure a new product the same
way every time instead of rediscovering it.

This is the counterpart to [CONFIGURATION.md](CONFIGURATION.md) (how a *running* system is
configured) and [TESTING.md](TESTING.md) (how it is run and tested). Nothing here touches
application config; everything here is repository configuration.

> **Nothing in the default path costs money or needs an API key.** Secrets appear only when you opt
> into cloud deploys, package publishing, or the hosted agentic workflows. A repo can be fully
> gated, boarded and loop-ready with zero secrets configured.

---

## 1. Two repo roles, two setups

The single most important decision is which of these you are configuring. They are not variations
of one setup — they have different gates and a different answer to "who may merge".

| | **Platform repo** (this one) | **Product repo** (built on the packages) |
|---|---|---|
| Identified by | `Plenipo.slnx` at the root | `Plenipo.*` PackageReferences, `workflow.json` |
| Who merges | **a human, always** — every level, every change | the merge gate, up to the recorded autonomy level |
| Branch protection | required CI contexts | required CI contexts **+ `PR gates`** |
| Deterministic gates | — | `pr-gates.mjs` + `merge-gate.mjs` |
| Autonomy block | **never** | `workflow.json` → `autonomy`, starting at `0` |
| Request surface | issue form + triage labels + `consumers.json` + conformance gate | files *into* the platform's surface |
| Board | optional | required — the loops read it, not `PLAN.md` |

> **Never install autonomy in the platform repo.** A product merging its own feature risks one
> product. The platform merging its own change risks every product that consumes it, including ones
> that have not upgraded yet.

---

## 2. The short version — which agents to run, in order

Each of these is a skill that owns its own procedure. Run them in this order; do not hand-roll the
steps they cover.

| # | Command | Role | What it configures |
|---|---|---|---|
| 1 | `/plenipo:launch` | product | The whole chain in one attended run — scout → define → shape → scaffold → **setup**. Pauses once, for the go/no-go and the brand name. Prefer this. |
| — | *or run the chain by hand:* | | |
| 1a | `/deliver:scaffold-product` | product | Repo shell, skeleton, `workflow.json`, `.claude/settings.json`, remote repo (asks first). |
| 1b | `/define:sync-backlog` | product | Epic + feature issues, sub-issue links, every card boarded in `Backlog` with a build order. |
| 1c | `/deliver:install-runbook` | product | `RUNBOOK.md`, integration fixture, `.http` catalog, evals, `.claude/launch.json`. |
| 1d | `/harness:install-agent-config` | both | `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md`, path-scoped instructions. |
| 2 | `/plenipo:setup` | product | **The GitHub configuration proper**: labels, the `autonomy` block, both gate scripts and their workflows, `CODEOWNERS`, branch protection, permission allow/deny list. |
| 3 | `/harness:install-github-agentic-workflows` | both, optional | gh-aw cloud triage + PR-intent review. **Human-invoked** (`disable-model-invocation: true`). |
| 4 | `/steward:install-request-surface` | **platform only** | Platform-request issue form, triage labels, `consumers.json`, consumer-conformance gate. **Human-invoked.** |
| ✔ | `/harness:validate-product` | product | Read-only audit of everything above. Run it after, and after every platform upgrade. |

Then the timers take over — see [§11](#11-running-the-loops).

> Re-run `/plenipo:setup` after every platform upgrade. Upgrades move check names and package pins,
> and a required context that no longer exists is a PR that waits forever.

---

## 3. Create the repo and fix its settings

```bash
gh repo create "$OWNER/$NAME" --public --source . --remote origin --push
```

Then the repository settings that the rest of this document assumes. These are one `gh api` call —
doing it by hand in the UI is where drift between repos starts:

```bash
gh api --method PATCH "repos/$OWNER/$NAME" \
  -F allow_squash_merge=true \
  -F allow_merge_commit=false \
  -F allow_rebase_merge=false \
  -F delete_branch_on_merge=true \
  -F allow_auto_merge=false \
  -F has_wiki=false \
  -F has_projects=true
```

Why each matters:

- **Squash only** — one issue, one commit on `main`. The merge gate squashes; making it the only
  option keeps history readable and revert cheap.
- **Delete branch on merge** — the loop opens a branch per issue; without this a product accumulates
  hundreds of stale refs within a week.
- **`allow_auto_merge=false`** — GitHub's auto-merge waits only for the conditions *it* knows about,
  so a PR can merge while the review is still running. It races the merge gate. **Never enable both.**

Actions permissions (Settings → Actions → General), which have no clean `gh` equivalent:

- Workflow permissions: **Read repository contents** by default. Each workflow that needs more
  declares it in its own `permissions:` block — see `publish.yml` for the pattern.
- **"Allow GitHub Actions to create and approve pull requests": off.** An Actions-approved PR
  satisfies a required-reviewers rule with no human involved, which quietly removes the gate.

---

## 4. Labels

Three families, three owners. All the commands are `--force`, so re-running is safe.

**The loop vocabulary** (`/plenipo:setup` creates these) — the state machine the timers steer by:

```bash
for L in "agent:ready:0E8A16" "agent:in-progress:FBCA04" "agent:blocked:B60205" \
         "agent:done:6E7781" "agent:needs-triage:D4C5F9" "agent:approved:0E8A16" \
         "agent:changes-requested:D93F0B" "human-hold:B60205" "human-approved:5319E7" \
         "needs-human:B60205" "type:bug:D73A4A" "type:enhancement:A2EEEF" \
         "regression:D73A4A" "security:B60205"; do
  gh label create "${L%:*}" --color "${L##*:}" --force
done
```

**The backlog taxonomy** (`/define:sync-backlog` creates these): `type:epic`, `type:feature`,
`priority:p0…p3`, `scope:*`, `seam:module|tool|tab|connector|role|host|frontend`, `approval-gated`,
`orphaned`. Do not duplicate them in the loop list.

**The platform triage taxonomy** (`/steward:install-request-surface`, platform repo only):
`platform-request`, `needs-triage`, `triage:already-possible`, `triage:product-scope`,
`triage:accepted`, `triage:deferred`, `triage:rejected`, `demand:multi`, plus one `from:<product>`
label per registered consumer.

Two labels carry real authority and are worth knowing by heart:

| Label | Effect |
|---|---|
| `human-hold` | the merge gate refuses the PR unconditionally, at any autonomy level |
| `human-approved` | the documented override for a `pr-gates` evidence failure — a human vouching for the evidence |

`from:<product>` labels are **provenance**: they are what an agentic workflow's `approval-labels`
list trusts. Only the router may apply them; a label allowlist is a security boundary, not decoration.

---

## 5. The project board (Projects v2)

The loops read the board, not `PLAN.md`. A board that lies is worse than no board.

```bash
gh auth refresh -s project          # the `project` scope is separate; without it every write fails
gh project create --owner "$OWNER" --title "$NAME"
```

`Status` is a single-select with exactly these five options, in this order, and each transition has
exactly one owner:

`Backlog` → `Ready` → `In Progress` → `In Review` → `Done`

| Transition | Owner |
|---|---|
| → `Backlog` | `/define:sync-backlog`, **on first boarding only** |
| `Backlog` → `Ready` | `/shape:design-product`, once the architecture delta for that feature exists |
| `Ready` → `In Progress` → `In Review` | `/deliver:work-next-issue` |
| → `Done` | the merged PR that closes the issue |

Required fields — a sync fails closed without them:

```bash
gh project field-create "$NAME" --owner "$OWNER" --name "Build order" --data-type NUMBER
# optional, but they make the board readable without opening sub-issues:
gh project field-create "$NAME" --owner "$OWNER" --name "Epic"  --data-type TEXT
gh project field-create "$NAME" --owner "$OWNER" --name "Seam"  --data-type SINGLE_SELECT
gh project field-create "$NAME" --owner "$OWNER" --name "Proof" --data-type SINGLE_SELECT
```

`Build order` is derived — `epic index × 100 + capability index × 10`, recomputed on every sync. The
gap is deliberate: inserting a capability renumbers nothing, so the board does not churn.

Record the board in `workflow.json` → `github.project` so every later skill finds it.

---

## 6. Branch protection — the "branch locks"

This is the step that makes everything else real. **On an unprotected repo the merge gate's
`checks_green` reads an empty check list and passes vacuously** — every downstream gate becomes
decorative while still reporting green.

### 6.1 Read the real check names first

`contexts` are matched against the **job `name:` string**, not the job id and not the workflow name.
A context that never appears is a required check that never reports, and GitHub waits forever.

```bash
gh pr checks <a-real-pr-number>     # copy the names exactly as printed
```

This repo's `ci.yml` declares three jobs, and its protection requires precisely those strings:

| Job id in `ci.yml` | `name:` → required context |
|---|---|
| `dotnet` | `.NET build, test & image scan` |
| `package` | `Package consumption smoke` |
| `frontend` | `Frontend lint, test, build & E2E` |

A product repo adds a fourth from `agent-gates.yml`: **`PR gates`**.

### 6.2 Apply it

```bash
gh api --method PUT "repos/$OWNER/$NAME/branches/$DEFAULT/protection" --input - <<'JSON'
{
  "required_status_checks": { "strict": true, "contexts": ["PR gates", "build"] },
  "enforce_admins": false,
  "required_pull_request_reviews": null,
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON
```

> On Windows PowerShell the heredoc will not parse — write the JSON to `protection.json` and pass
> `--input protection.json`, or run the block from Git Bash.

| Field | Setting | Why |
|---|---|---|
| `contexts` | the exact names from §6.1 | anything else waits forever |
| `strict` | `true` | the branch must be current with base; stops a PR that was green against a stale `main` |
| `required_pull_request_reviews` | `null` **or** a review rule — see below | this is the fork in the road |
| `enforce_admins` | `false` | leaves a human an escape hatch; set `true` once the product is stable |
| `allow_force_pushes` / `allow_deletions` | `false` | non-negotiable — they are how history and gates disappear |
| `restrictions` | `null` | required key on the PUT; use it only if you push-restrict by team |

Verify, always — a protection call that 422s silently leaves you unprotected:

```bash
gh api "repos/$OWNER/$NAME/branches/$DEFAULT/protection" --jq '.required_status_checks.contexts'
```

### 6.3 The one decision you cannot have both ways

**Requiring an approving review and expecting the scheduled merger to work are mutually exclusive.**

- **Require reviews** → merging stays manual. Correct for the platform repo and for any product at
  autonomy level 0. Add *Require review from Code Owners* to force a named human onto spine changes.
- **No required reviews** → the merge gate is the gate, and it is only as good as the required
  status checks you just configured.

Configuring both and assuming the automation still runs is how a queue silently stops.

---

## 7. The deterministic gates (product repos)

Skill prose is advisory in every tool that reads it; a required status check is not. `/plenipo:setup`
copies two node scripts and two workflows into the repo **verbatim** — the property that matters is
that the same file runs in CI and locally, so "improving" one in transit breaks it.

| Where | Script / workflow | Gates |
|---|---|---|
| CI, as a **required check** on every push | `.github/scripts/pr-gates.mjs` via `.github/workflows/agent-gates.yml` | `closes_an_issue` · `has_runtime_evidence` · `has_red_before_green` · `spine_untouched` |
| A `*/15` schedule, and `/plenipo:ship` locally | `.github/scripts/merge-gate.mjs` via `.github/workflows/agent-merge.yml` | `is_loop_pr` · `not_draft` · `checks_exist` · `checks_green` · `mergeable` · `no_blocking_review` · `agent_approved` · `no_human_hold` · `main_is_green` · `level_permits` · `under_cap` |

The split is not arbitrary. The first four are assertions about **the body and the diff**, so they
run where they cannot be skipped, including on a human's PR. The rest are assertions about **the
world right now** — is CI green, is a hold set, is `main` healthy — so they are re-read at merge time
rather than trusted from an earlier event. `agent-merge.yml` is deliberately *not* `pull_request`
-triggered for exactly this reason.

`spine_untouched` is **content-based, not path-based**: *adding* a `HasQueryFilter` is ordinary
feature work, *deleting or editing* one is a tenant-isolation change. A path rule would either block
every migration or catch nothing.

### 7.1 The PR body contract

`pr-gates.mjs` reads the PR body, so the body is part of the gate. A PR must carry:

```markdown
Closes #<n>

## Runtime evidence
POST /api/agui/<module> streamed RUN_FINISHED, no RUN_ERROR.

## Regression test
<Product>.IntegrationTests.<Case> — seen red before the fix, green after.
```

`/deliver:work-next-issue` writes this shape. A body missing a section is a red check, not a nudge.

### 7.2 Prove both scripts before trusting either

**A check never seen red may be asserting nothing** — and installing an inert gate is worse than
having none, because a green check then reads as "someone verified this".

```bash
# pr-gates: red first
printf 'diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n-  b.HasQueryFilter(x => true);\n' > /tmp/d
PR_HEAD_REF=feat/1-x PR_BODY='' node .github/scripts/pr-gates.mjs /tmp/d      # expect exit 1, 4 gates

# then green
PR_HEAD_REF=feat/1-x PR_LABELS=human-approved \
  PR_BODY="$(printf 'Closes #1\n## Runtime evidence\nPOST /api/agui/x streamed RUN_FINISHED, no RUN_ERROR.\n## Regression test\nXTests.Y seen red before, green after.\n')" \
  node .github/scripts/pr-gates.mjs /tmp/d                                    # expect exit 0

# merge-gate: it must refuse everything at level 0
node .github/scripts/merge-gate.mjs                    # every open PR BLOCKed on level_permits
```

If the first command exits 0, stop and fix the wiring before going further.

### 7.3 The kill switch

Set the repository **variable** `AGENT_AUTOMERGE=off` (Settings → Secrets and variables → Actions →
Variables) to stop the scheduled merger without a commit, a revert, or a redeploy.

```bash
gh variable set AGENT_AUTOMERGE --body off
```

---

## 8. Autonomy level

One number in `workflow.json`, read by `merge-gate.mjs` and by `/plenipo:ship`. It is the
authoritative control on merging — the permission deny-list is the belt, this is the braces.

```jsonc
"autonomy": {
  "level": 0,              // 0 nothing · 1 docs+tests · 2 features on review · 3 unattended
  "maxOpenPRs": 3,         // build back-pressure: the ceiling /plenipo:deliver stops at
  "maxMergesPerTick": 2,   // blast radius per ship tick
  "readyFloor": 3,         // /plenipo:define refills below this
  "maxIssuesPerSweep": 8,  // /plenipo:test flood protection
  "maxNewCapabilities": 5  // /plenipo:define scope cap per tick
}
```

| Level | May merge | Requires |
|---|---|---|
| **0** | nothing — review and label only | the default for any repo without a proven runbook |
| **1** | docs, `RUNBOOK.md`, test-only additions, a green version bump | every gate except `agent_approved` |
| **2** | product features | all gates, including `agent:approved` from the PR reviewer |
| **3** | as level 2, unattended, inside a revert budget | all gates, plus a clean level-2 stretch |

**Never at any level:** anything in the platform repo, and anything `spine_untouched` catches. Those
do not get safer as a track record improves, because the cost of being wrong does not shrink.

Absent means **0**. Only a human writes this field, only upward one step at a time, and never
because the loop has been doing well — that judgement is the one thing a loop is structurally unfit
to make.

---

## 9. CODEOWNERS and the permission list

`CODEOWNERS` is the human-visibility half of the spine policy; `pr-gates.mjs` is the enforcing half.
Keep both — the check is what stops an agent, the ownership is what tells you it happened. It only
*blocks* if you also enable *Require review from Code Owners* in branch protection.

```
* @<owner>

# The spine — RBAC before the model, approval-first writes, tenant isolation, audit, secrets.
/src/**/Authorization/     @<owner>
/src/**/Approvals/         @<owner>
/src/**/Persistence/       @<owner>

# The harness itself. An agent that can edit its own gates has no gates.
/.github/                  @<owner>
/CODEOWNERS                @<owner>
/nuget.config              @<owner>
/workflow.json             @<owner>
```

`.claude/settings.json` in a product repo carries the matching permission list: allow what a tick
genuinely needs (`gh`, `git`, `dotnet`, `docker`, `aspire`, `node`, `npm`, `npx`, `curl`) so a timer
never blocks on a prompt nobody is there to answer, and deny the destructive verbs
(`gh pr merge`, `gh pr review`, `git push --force`, `gh repo delete`,
`gh api --method DELETE`, `docker volume rm`).

> The deny list matches the Bash string only. It stops an improvised `gh pr merge`; it cannot see
> the merge *inside* `node .github/scripts/merge-gate.mjs --merge`. That is why `autonomy.level`,
> not the deny list, is the authoritative control.

**Read the owner from `workflow.json` or `gh api user` — never hardcode it.** A template that works
for one GitHub account is broken for every other one.

---

## 10. Secrets, variables, and environments

Only configure the block you actually use. The local build/gate/merge path needs **none** of them.

### 10.1 Cloud deploy (Azure, OIDC — no stored cloud password)

Settings → Secrets and variables → Actions. Per-environment values belong on the GitHub Environment,
not the repo.

| Name | Scope | From |
|---|---|---|
| `AZURE_CLIENT_ID` | repo/env | `terraform output cicd_identity_client_id` |
| `AZURE_TENANT_ID` | repo/env | the deployment tenant |
| `AZURE_SUBSCRIPTION_ID` | repo/env | the target subscription |
| `ACR_LOGIN_SERVER` | repo/env | `terraform output acr_login_server` |
| `TF_BACKEND_RESOURCE_GROUP` / `TF_BACKEND_STORAGE_ACCOUNT` / `TF_BACKEND_CONTAINER` | repo | the remote-state storage |
| `TF_STATE_KEY` | env | one state blob per environment |

Environments (Settings → Environments): `staging` with no reviewers (auto-deploys on push to `main`),
`production` **with required reviewers** so `deploy.yml` pauses, `development` for the PR plan.

The federated credentials are created by the `cicd-identity` Terraform module. When a job runs
*inside* an environment the OIDC subject becomes the `repo:OWNER/REPO:environment:<env>` form — so
`github_environments` must list every environment you deploy to, or the login fails with a claim
mismatch. Full table in [`.github/workflows/README.md`](../.github/workflows/README.md).

### 10.2 Publishing (platform repo)

`NPM_TOKEN` for `@plenipo/ui`; GitHub Packages uses the built-in `GITHUB_TOKEN`; nuget.org uses
**Trusted Publishing** (OIDC), so there is no NuGet secret.

> The nuget.org trust policy is keyed to repository + workflow file **with no environment**. Adding
> `environment:` to the publish job breaks the claim match and the push fails. Do not "harden" that
> job with an environment gate.

### 10.3 Agentic workflows

| Name | Type | Notes |
|---|---|---|
| `COPILOT_GITHUB_TOKEN` | secret | fine-grained PAT, account with a Copilot licence, **Copilot Requests: Read**. Not an OAuth `gho_…` token. |
| `GH_AW_ROUTER_APP_ID` | variable | the cross-repo router App |
| `GH_AW_ROUTER_APP_PRIVATE_KEY` | secret | its private key |
| `AGENT_AUTOMERGE` | variable | `off` disables the scheduled merger |

Never put a credential in workflow frontmatter, in a variable, or in a committed file.

---

## 11. Agentic workflows (optional, both roles)

GitHub Agentic Workflows (`gh-aw`) give cloud-side triage and PR-intent review — useful for when the
machine that writes the code is asleep. They are **optional**: `/plenipo:ship` already reviews
locally, for free, under the subscription you have. Install via
`/harness:install-github-agentic-workflows`; do not hand-roll one.

```bash
gh extension install github/gh-aw
gh aw init --engine copilot
gh aw validate --strict
gh aw compile --validate --actionlint --zizmor --poutine --approve
```

The mechanics that matter:

- **The source is the `.md`; the `.lock.yml` is generated and SHA-pinned.** Never edit a lock file —
  the next compile discards it. Commit both. This repo carries three:
  `platform-request-triage`, `platform-pr-intent-review`, `platform-release-impact`.
- **The agent is read-only.** Every write is declared in `safe-outputs:` and constrained by type,
  target, maximum, and label/repository allowlist.
- **Issues, PRs, comments and linked pages are untrusted input.** Keep `min-integrity: approved`.
- **Keep `allowed-events: [COMMENT]` on review workflows.** A model must never become a merge gate —
  it can leave a comment, it cannot Approve, and it therefore satisfies no required-reviewers rule.
- **Cross-repo routing uses a GitHub App, never a broad PAT**: metadata read, `Contents: read`,
  `Issues: read/write`, `Pull requests: read/write` — no administration, no workflows, no contents
  write. Its `repositories:` list must equal the explicit safe-output target list.
- **Prove writes before enabling them**: `gh aw compile --staged --approve`, dispatch against a
  disposable issue, read the action summary, then go live. Compiler-green is not runtime proof.
- **Actions are pinned** in `.github/aw/actions-lock.json`; `.poutine.yml` configures the supply-chain
  scanner.

---

## 12. Platform-repo extras

Installed by `/steward:install-request-surface`:

- **`.github/ISSUE_TEMPLATE/platform-request.yml`** — the structured form products file against. Do
  not soften its required fields; "which seam you tried" is the field that makes the queue
  survivable. Add `config.yml` with `blank_issues_enabled: false` so the contract cannot be bypassed.
- **`consumers.json`** — the registry of products that must not break. Each entry records the repo,
  solution file, ref, module id, and whether it is `required`. Mark a stale consumer
  `"conformance": false` with a note rather than deleting it: a permanently red gate gets ignored,
  which is worse than no gate.
- **`consumer-conformance.yml`** — builds and tests every registered consumer against the release
  candidate before a platform change can merge. It swaps versions with `-p:PlenipoVersion=<rc>`, so
  a consumer must centralize its platform version in one MSBuild property to be eligible.

Prove that gate in both directions — green against an unmodified platform, and red when something
genuinely breaks — before marking any consumer `required`.

---

## 13. Dependency and supply-chain hygiene

`.github/dependabot.yml` covers four ecosystems: NuGet at the root (Central Package Management means
one `Directory.Packages.props`), npm at the **pnpm workspace root** (`/frontend` — pointing at a
member package bumps `package.json` without the lockfile and every PR dies on
`ERR_PNPM_OUTDATED_LOCKFILE`), GitHub Actions, and the API Dockerfile base images. Minor/patch are
grouped to cut noise; majors arrive individually so they get a real review.

CI additionally runs a Trivy image scan; the agentic workflow compiler runs `actionlint`, `zizmor`
and `poutine` over the workflow set.

---

## 14. Running the loops

Once §2 is complete, these are the commands that drive a product repo:

```bash
/loop 20m /plenipo:deliver    # one issue → branch → runtime proof → PR
/loop 30m /plenipo:ship       # adversarial review, then the gate decides what merges
/loop 3h  /plenipo:test       # sweep for defects, file them
/loop 6h  /plenipo:define     # refill the Ready column when it runs low
```

`ship` reports `Blocked` if branch protection is missing or the autonomy level is unrecorded — which
is the design working: the loop refuses to run in a repo where its gates would be vacuous.

> **Never run `ship` in the session that ran `deliver`.** The agent boundary is the only thing making
> maker ≠ checker true. A different session, or the timer.

---

## 15. Verification checklist

Run this list before pointing a timer at a new repo. Every line is a command, not a judgement.

| Check | Command | Expected |
|---|---|---|
| Protection exists with the right contexts | `gh api repos/$OWNER/$NAME/branches/main/protection --jq '.required_status_checks.contexts'` | exactly the names from `gh pr checks` |
| Force-push and delete are off | `... --jq '.allow_force_pushes.enabled, .allow_deletions.enabled'` | `false false` |
| `pr-gates` fails on a bad PR | §7.2 first command | exit 1, four gates named |
| `pr-gates` passes on a good PR | §7.2 second command | exit 0 |
| The merge gate refuses at level 0 | `node .github/scripts/merge-gate.mjs` | every PR `BLOCK`ed on `level_permits` |
| Autonomy is recorded | `jq .autonomy.level workflow.json` | a number a human chose |
| Labels exist | `gh label list --limit 100` | the three families from §4 |
| The board has its fields | `gh project field-list "$NAME" --owner "$OWNER"` | `Status` (5 options) + `Build order` |
| Merge settings | `gh api repos/$OWNER/$NAME --jq '.allow_squash_merge, .allow_auto_merge, .delete_branch_on_merge'` | `true false true` |
| Everything else | `/harness:validate-product` | pass |

---

## 16. Common pitfalls

| Pitfall | Consequence | Do instead |
|---|---|---|
| Copying the gate workflows but skipping branch protection | every gate reads nothing and passes vacuously; the loop merges freely | §6 is the point of §7 |
| Naming a required context that does not exist | PRs wait forever on a check that never reports | read the names from `gh pr checks` |
| Editing a gate script "while copying it" | CI and the local verb disagree about what green means | copy verbatim; change the asset, then re-copy |
| Enabling GitHub auto-merge as well | a PR merges while review is still running | `allow_auto_merge=false`; the gate is the only merger |
| Requiring reviews *and* expecting the merger to run | the queue stops silently | pick one — §6.3 |
| Starting at autonomy level 2 | a product with no track record merging its own features | 0, then earn each step |
| Inferring the level from a good streak | the loop grants itself permission it was never given | read the field; absent means 0 |
| Installing the cloud agentic surface first | a secret, a bill, and flags nobody verified | the local reviewer is the default |
| Editing a `.lock.yml` | the next compile discards it | edit the `.md`, then compile |
| Letting a review workflow Approve or Request-changes | a model becomes an unsafe policy gate | `allowed-events: [COMMENT]` |
| Skipping the runbook because the code builds | nothing can produce runtime evidence, so `has_runtime_evidence` blocks every PR forever | `/deliver:install-runbook` first |
| Registering a stale consumer as `required` | a permanently red gate everyone learns to ignore | `"conformance": false` plus a note |
| Hardcoding the owner in CODEOWNERS or a template | works for exactly one GitHub account | read it from `workflow.json` or `gh api user` |
| Treating this as install-once | upgrades move check names and pins; the gates rot silently | re-run `/plenipo:setup` after every platform upgrade |

---

## See also

- [`.github/workflows/README.md`](../.github/workflows/README.md) — the CI/CD workflows, the full
  OIDC secret table, and the federated-credential subjects.
- [CONTRIBUTING.md](../CONTRIBUTING.md) — repo layout, build/test commands, code conventions.
- [SECURITY.md](../SECURITY.md) and [AGENT_SECURITY.md](AGENT_SECURITY.md) — the security model the
  spine paths in `CODEOWNERS` protect.
- [CONFIGURATION.md](CONFIGURATION.md) — configuring a running system (this document is about
  configuring its repository).
- [BUILDING_A_PRODUCT.md](../BUILDING_A_PRODUCT.md) — what goes *in* a product repo once its GitHub
  side is set up.
