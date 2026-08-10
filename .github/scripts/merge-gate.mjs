#!/usr/bin/env node
// The merger. Deterministic (verification ladder L1/L2) — no dependencies beyond `gh`, node >= 18.
//
//   node .github/scripts/merge-gate.mjs                 evaluate every open PR, print verdicts
//   node .github/scripts/merge-gate.mjs --pr 131         evaluate one
//   node .github/scripts/merge-gate.mjs --merge          merge what passes, up to the cap
//   node .github/scripts/merge-gate.mjs --fixture f.json evaluate fixture data (used to test itself)
//
// This file is the ONE implementation of the merge gates. `/plenipo:ship` runs it rather than
// re-deriving the list in prose, and `agent-merge.yml` runs it on a schedule so merging keeps
// working when the machine that wrote the code is off. An agent can be argued out of a judgement;
// an exit code cannot.
//
// Required checks are necessary but not a root of trust for changes to the workflows themselves:
// a pull_request workflow wrapper comes from the proposed merge commit. For control-plane changes
// this merger downloads and runs `pr-gates.mjs` from the protected base again before merging.

import { readFileSync, existsSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createApprovalProofClient } from './approval-proof.mjs';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const i = argv.indexOf(name);
  return i === -1 ? undefined : argv[i + 1];
};

const DO_MERGE = flag('--merge');
const ONE_PR = value('--pr');
const FIXTURE = value('--fixture');

// Fixture data describes pull requests that do not exist. Nothing driven by it may reach the
// network — so `--fixture` degrades `--merge` to a simulation rather than being ignored by it.
// Without this, `--fixture x.json --merge` tries to squash-merge pull requests numbered 901-910 in
// whatever repo it happens to be run from.
const SIMULATE = Boolean(FIXTURE);

const runGh = (args) =>
  spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
const gh = (args) => {
  const r = runGh(args);
  if (r.error || r.status !== 0) {
    throw new Error(`gh ${args.join(' ')} failed:\n${r.error?.message || r.stderr || r.stdout}`);
  }
  return r.stdout;
};

// Same call, but a failure is reported instead of thrown. Used for the steps whose failure must not
// take the rest of the queue down with them: closing a linked issue after a merge has already
// landed, and updating a stale branch. Throwing there would fail the run and skip every remaining
// pull request over something that is recoverable next tick.
const ghSoft = (args) => {
  const r = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  return { ok: r.status === 0, out: (r.stderr || r.stdout || '').trim().split('\n')[0] ?? '' };
};

// ── Policy, read from the repo — never inferred ───────────────────────────────
// An agent that decides it has earned autonomy is the self-approving loop wearing a different hat.
// Absent config means level 0: review and label, merge nothing.
const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const autonomy = cfg.autonomy ?? {};
const LEVEL = Number.isInteger(autonomy.level) ? autonomy.level : 0;
const MAX_MERGES = autonomy.maxMergesPerTick ?? 2;

const LOOP_BRANCH = /^(feat|fix|chore)\//;
const HOLD_LABELS = ['human-hold', 'needs-human', 'agent:blocked'];
// Docs, tests and the runbook are the only class a level-1 product may land on its own.
const LOW_RISK = [/\.md$/i, /^tests\//, /\.http$/i, /^\.http$/i];
// Changes to the loop's own controls need a verdict run by the policy already on the protected
// base. The PR may propose the next policy, but it cannot use that proposal to approve itself.
const CONTROL_PATHS = [
  /^\.github\//,
  /^\.claude\//,
  /^\.codex\//,
  /^eng\//,
  /(^|\/)\.claude-plugin\//,
  /^workflow\.json$/,
  /(^|\/)AGENTS\.md$/,
  /(^|\/)CLAUDE\.md$/,
  /(^|\/)CODEOWNERS$/,
  /^\.gitattributes$/,
];

// ── Platform repos are gated differently, not more leniently ─────────────────
// A product merge risks one product; a platform merge risks every product built on it, and that
// asymmetry GROWS with each consumer rather than shrinking with a good track record. So the platform
// has no autonomy level to earn — it has a stronger verifier: consumer-conformance.yml packs the
// platform as a release candidate and rebuilds every registered consumer against it.
const IS_PLATFORM = String(cfg.stage ?? cfg.kind ?? 'product').toLowerCase() === 'platform';
const CONFORMANCE_CHECK = /consumer.?conformance|conformance verdict/i;
// Keep this exact path list aligned with consumer-conformance.yml. Requiring a check whose workflow
// was skipped is a deadlock; accepting a source change without that check is a consumer break.
const CONFORMANCE_PATHS = [/^src\//, /^Directory\.(?:Packages|Build)\.props$/];
const SURFACE_RE = /^\s*(?:public[- ])?surface:\s*(additive|breaking|none)\b/im;
const INFRA_PATHS = [/^infra\//];
const TERRAFORM_CHECK = /terraform|fmt\s*\/\s*validate\s*\/\s*plan/i;

const PR_FIELDS = [
  'number', 'title', 'body', 'isDraft', 'headRefName', 'headRefOid', 'baseRefName', 'labels',
  'mergeable', 'mergeStateStatus', 'reviewDecision', 'statusCheckRollup', 'files', 'changedFiles', 'author',
  // Only ever reported, never gated on. A queue that stops moving looks identical to a healthy one
  // in a run log that prints no ages — which is how this went unnoticed for weeks.
  'createdAt',
  // Read rather than parsed out of the body: this is the link GitHub itself acts on, so it also
  // covers an issue attached through the Development sidebar with no keyword in the text.
  'closingIssuesReferences',
].join(',');

// ── Load the pull requests ───────────────────────────────────────────────────
let prs;
let fixtureRequiredCheckContexts = null;
if (FIXTURE) {
  const fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));
  if (Array.isArray(fixture)) {
    prs = fixture;
  } else {
    prs = fixture.pullRequests ?? [];
    fixtureRequiredCheckContexts = fixture.requiredCheckContexts ?? null;
  }
} else if (ONE_PR) {
  prs = [JSON.parse(gh(['pr', 'view', ONE_PR, '--json', PR_FIELDS]))];
} else {
  prs = JSON.parse(gh(['pr', 'list', '--state', 'open', '--limit', '50', '--json', PR_FIELDS]));
}

// Branch protection, not every check that happens to appear in a rollup, defines the CI contract.
// Agentic review is deliberately separate: a provider outage means no approval label, not failed
// product CI. `gh pr checks --required` reads CheckRun.isRequired through the pull-request GraphQL
// surface, which the scheduled GITHUB_TOKEN can read. The Administration-only branch-protection
// REST endpoint cannot be read by that token and used to leave every scheduled merge green-but-idle.
const requiredContextsCache = new Map();
const infrastructureFailures = new Set();
function requiredContextsFor(pr) {
  if (FIXTURE) return { contexts: fixtureRequiredCheckContexts };
  if (requiredContextsCache.has(pr.number)) return requiredContextsCache.get(pr.number);

  let result;
  try {
    const args = ['pr', 'checks', String(pr.number), '--required', '--json', 'name'];
    const query = runGh(args);
    if (query.error || ![0, 1, 8].includes(query.status)) {
      throw new Error(`gh ${args.join(' ')} failed: ${query.error?.message || query.stderr || query.stdout}`);
    }

    const output = query.stdout.trim();
    if (!output && !(query.status === 1 && /no (required )?checks/i.test(query.stderr))) {
      throw new Error(`gh ${args.join(' ')} returned no parseable check data: ${query.stderr || '(empty output)'}`);
    }
    const required = output ? JSON.parse(output) : [];
    if (!Array.isArray(required)) throw new Error(`gh ${args.join(' ')} returned a non-array payload`);
    result = { contexts: [...new Set(required.map((check) => check.name).filter(Boolean))] };
  } catch (error) {
    const message = `could not read required checks for #${pr.number}: ${error.message.split('\n')[0]}`;
    infrastructureFailures.add(message);
    result = { error: message };
  }
  requiredContextsCache.set(pr.number, result);
  return result;
}

const approvalProof = FIXTURE ? null : createApprovalProofClient({ gh, runGh });
let repoSlug;
const diffCache = new Map();

function repository() {
  repoSlug ??= JSON.parse(gh(['repo', 'view', '--json', 'nameWithOwner'])).nameWithOwner;
  return repoSlug;
}

function diffFor(pr) {
  if (FIXTURE) return pr.diffError ? { error: pr.diffError } : { text: pr.diff ?? '' };
  const key = `${pr.number}:${pr.headRefOid ?? ''}`;
  if (diffCache.has(key)) return diffCache.get(key);
  let result;
  try {
    result = { text: gh(['pr', 'diff', String(pr.number), '--patch']) };
  } catch (error) {
    const message = `could not read the full diff for #${pr.number}: ${error.message.split('\n')[0]}`;
    infrastructureFailures.add(message);
    result = { error: message };
  }
  diffCache.set(key, result);
  return result;
}

// GitHub's `files[].path` is the destination path. Both headers are required to classify a rename
// out of `.github/` and a deletion whose destination is `/dev/null`.
function pathsFromDiff(diff) {
  const paths = [];
  for (const line of String(diff ?? '').split('\n')) {
    if (!line.startsWith('--- ') && !line.startsWith('+++ ')) continue;
    const path = line.slice(4).trim().replace(/^[ab]\//, '');
    if (path && path !== '/dev/null') paths.push(path);
  }
  return [...new Set(paths)];
}

function trustedPrGatesFor(pr, diff) {
  if (FIXTURE) {
    return pr.trustedPrGates === false
      ? { ok: false, why: 'the protected-base PR gate rejected this control-plane diff' }
      : { ok: true };
  }

  const scratch = mkdtempSync(join(tmpdir(), `plenipo-pr-gates-${pr.number}-`));
  const script = join(scratch, 'pr-gates.mjs');
  const patch = join(scratch, 'pr.diff');
  try {
    const source = gh(['api', '-H', 'Accept: application/vnd.github.raw+json',
      `repos/${repository()}/contents/.github/scripts/pr-gates.mjs?ref=${encodeURIComponent(pr.baseRefName)}`]);
    writeFileSync(script, source);
    writeFileSync(patch, diff);
    const labels = (pr.labels ?? []).map((label) => typeof label === 'string' ? label : label.name).join(',');
    const result = spawnSync(process.execPath, [script, patch], {
      encoding: 'utf8',
      env: {
        ...process.env,
        PR_BODY: pr.body ?? '',
        PR_HEAD_REF: pr.headRefName ?? '',
        PR_LABELS: labels,
      },
    });
    if (result.error || result.status !== 0) {
      const output = `${result.stderr || ''}\n${result.stdout || ''}`.trim().replace(/\s+/g, ' ').slice(0, 500);
      return { ok: false, why: output || result.error?.message || `base evaluator exited ${result.status}` };
    }
    return { ok: true };
  } catch (error) {
    const message = `could not execute protected-base PR gates for #${pr.number}: ${error.message.split('\n')[0]}`;
    infrastructureFailures.add(message);
    return { ok: false, why: message };
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

// ── Gates ────────────────────────────────────────────────────────────────────
function evaluate(pr) {
  const fail = [];
  const labels = (pr.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name).toLowerCase());
  const agentApproved =
    labels.includes('agent:approved') &&
    !['agent:changes-requested', ...HOLD_LABELS].some((label) => labels.includes(label));
  // GitHub's rollup keeps EVERY check run for the head commit, including superseded ones — a check
  // that failed and was then re-run green appears TWICE. Filtering the raw list makes a stale
  // FAILURE permanent: a pull request that ever went red could never merge again however green it
  // became, and the queue stops with a reason that reads like a real failure.
  //
  // Observed, not theorised: after a PR body edit re-triggered `Agent gates`, the rollup held
  //   PR gates | FAILURE | 01:29:48
  //   PR gates | SUCCESS | 01:31:56
  // and `gh pr checks` reported pass while this gate reported "PR gates not passing".
  //
  // EVERY rule below biases toward blocking, because the two failure directions are not
  // symmetrical: refusing a mergeable PR wastes a tick, while merging on a superseded green is
  // unrecoverable. An earlier version of this collapsed to "latest by startedAt", which merged a PR
  // whose re-run was still QUEUED — the queued entry has no timestamp, lost the comparison, and was
  // dropped. That sequence is routine here by design: `agent-gates.yml` re-triggers on `labeled`,
  // the reviewer adds `agent:approved`, and the merge cron fires minutes later.
  //
  // This keys on workflow+job (two workflows may both define `build`), cannot trust rollup ordering
  // (observed: the earliest-started entry appearing last), and treats `cancelled` as broken.
  const groups = new Map();
  for (const c of pr.statusCheckRollup ?? []) {
    // Job name alone collides across workflows; qualify it.
    const key = `${c.workflowName ?? ''}/${c.name || c.context || ''}`;
    (groups.get(key) ?? groups.set(key, []).get(key)).push(c);
  }

  const terminal = (c) => (c.conclusion || c.state || c.status || '').toUpperCase();
  const isPending = (c) => ['PENDING', 'IN_PROGRESS', 'QUEUED', 'WAITING', ''].includes(terminal(c));
  // StatusContext entries carry no startedAt at all; legacy commit statuses only have createdAt.
  const when = (c) => c.completedAt ?? c.startedAt ?? c.createdAt ?? '';

  const checks = [...groups.values()].map((runs) => {
    // A re-run in flight means the verdict is not settled, whatever an older run concluded. Report
    // the pending one so `checks_green` says "still running" rather than merging on stale green.
    const inFlight = runs.find(isPending);
    if (inFlight) return inFlight;

    // Latest terminal run wins; on a tie — same-second timestamps are common, one event triggering
    // several runs — prefer the non-SUCCESS, so an ambiguous pair never resolves to "mergeable".
    return runs.reduce((best, c) => {
      if (when(c) > when(best)) return c;
      if (when(c) < when(best)) return best;
      return terminal(best) === 'SUCCESS' ? c : best;
    });
  });

  const files = (pr.files ?? []).map((f) => f.path ?? f.filename ?? '');
  const filesAreComplete = !Number.isInteger(pr.changedFiles) || files.length >= pr.changedFiles;
  const isLoopBranch = LOOP_BRANCH.test(pr.headRefName ?? '');
  const diff = isLoopBranch ? diffFor(pr) : { text: '' };
  const allPaths = [...new Set([...files, ...pathsFromDiff(diff.text)])];
  const conformanceRequired = !filesAreComplete || allPaths.some((file) => CONFORMANCE_PATHS.some((re) => re.test(file)));
  const infraRequired = !filesAreComplete || allPaths.some((file) => INFRA_PATHS.some((re) => re.test(file)));
  // A diff-read error is classified conservatively as a control change. The explicit failure below
  // keeps it from becoming a level-1 docs PR by omission.
  const controlsChanged = Boolean(diff.error) || !filesAreComplete ||
    allPaths.some((file) => CONTROL_PATHS.some((re) => re.test(file)));

  const state = (c) => (c.conclusion || c.state || c.status || '').toUpperCase();
  const context = (c) => c.name || c.context || '';
  const required = requiredContextsFor(pr);
  const requiredChecks = required.contexts ? checks.filter((c) => required.contexts.includes(context(c))) : checks;
  const missingRequired = required.contexts?.filter((name) => !checks.some((c) => context(c) === name)) ?? [];
  const pending = requiredChecks.filter((c) => ['PENDING', 'IN_PROGRESS', 'QUEUED', 'WAITING', ''].includes(state(c)));
  const broken = requiredChecks.filter((c) => ['FAILURE', 'ERROR', 'CANCELLED', 'TIMED_OUT', 'ACTION_REQUIRED'].includes(state(c)));

  const isLowRisk = filesAreComplete && !controlsChanged && allPaths.length > 0 &&
    allPaths.every((file) => LOW_RISK.some((re) => re.test(file)));
  const changeClass = isLowRisk ? 'low-risk' : 'feature';

  if (!isLoopBranch) fail.push(`is_loop_pr: "${pr.headRefName}" is not a loop branch — not ours to merge`);
  if (!/plenipo-agent/.test(pr.body ?? '')) fail.push('is_loop_pr: the body carries no plenipo-agent envelope');
  if (pr.isDraft) fail.push('not_draft: the PR is a draft');
  if (diff.error) fail.push(`diff_inspected: ${diff.error}`);
  if (required.error) fail.push(`checks_configured: ${required.error}`);
  else if (missingRequired.length) {
    fail.push(`checks_green: required check(s) missing from the rollup: ${missingRequired.join(', ')}`);
  } else if (requiredChecks.length === 0) {
    fail.push('checks_exist: no required status checks ran — green would mean nothing');
  }
  if (pending.length) fail.push(`checks_green: ${pending.length} check(s) still running`);
  if (broken.length) fail.push(`checks_green: ${broken.map(context).join(', ')} not passing`);
  if (pr.mergeable && pr.mergeable !== 'MERGEABLE') fail.push(`mergeable: mergeable=${pr.mergeable}`);
  // `BEHIND` is staleness, not a defect, and it is the ONE mergeStateStatus this script can repair
  // by itself — which is why it is no longer lumped in with DIRTY and BLOCKED. Those need someone
  // else: DIRTY needs a human or the author to resolve a real conflict, BLOCKED needs a branch
  // protection rule satisfied. BEHIND needs one API call.
  //
  // Treating all three as terminal is what turned this queue into a ratchet, observed across six
  // repos: the moment anything landed on main, every other open PR went BEHIND, nothing in the loop
  // had ever called `gh pr update-branch` (zero occurrences in the whole marketplace), and so the
  // queue could absorb exactly one merge and then stopped forever. Fourteen of twenty-five open
  // pull requests were sitting on this single reason, three of them already carrying
  // `agent:approved` — the system had decided they should merge and then could not.
  const mergeState = String(pr.mergeStateStatus ?? '').toUpperCase();
  const stale = mergeState === 'BEHIND';
  // UNSTABLE means a non-required check failed. Required checks were read independently above, so
  // rejecting it would let an advisory model/provider outage become a second, accidental CI gate.
  // UNKNOWN and HAS_HOOKS remain unsettled; DIRTY and BLOCKED remain real merge blockers.
  if (mergeState && !['CLEAN', 'BEHIND', 'UNSTABLE'].includes(mergeState)) {
    fail.push(`mergeable: mergeStateStatus=${mergeState}`);
  }
  if (pr.reviewDecision === 'CHANGES_REQUESTED') fail.push('no_blocking_review: a review requested changes');
  if (!labels.includes('agent:approved')) fail.push('agent_approved: no `agent:approved` label — nothing has reviewed this');
  if (labels.includes('agent:changes-requested')) fail.push('agent_approved: `agent:changes-requested` is still set');
  for (const h of HOLD_LABELS) if (labels.includes(h)) fail.push(`no_human_hold: \`${h}\` is set`);
  // Every unattended merge needs approval-specific provenance. A successful reviewer run is not
  // sufficient: requesting changes is also a successful workflow execution, and a label can be
  // supplied independently. The proof binds the applied output to this head/base/body revision.
  if (agentApproved) {
    const trusted = FIXTURE
      ? (pr.trustedApproval === false
          ? { ok: false, why: 'no approval-specific proof covers this revision' }
          : { ok: true })
      : approvalProof.prove(pr);
    if (trusted.infrastructure) infrastructureFailures.add(trusted.why);
    if (!trusted.ok) fail.push(`trusted_agent_approval: ${trusted.why}`);
  }
  if (agentApproved && controlsChanged && !diff.error) {
    const trustedGates = trustedPrGatesFor(pr, diff.text);
    if (!trustedGates.ok) fail.push(`trusted_pr_gates: ${trustedGates.why}`);
  }
  if (LEVEL === 0) fail.push('level_permits: autonomy level 0 merges nothing — a human decides');
  else if (LEVEL === 1 && changeClass !== 'low-risk') {
    fail.push('level_permits: level 1 may merge docs, tests and the runbook only');
  }

  // ── Platform-only gates ────────────────────────────────────────────────────
  // `checks_green` CANNOT stand in for consumers_green, and this is the whole reason it is a named
  // gate rather than a comment: consumer-conformance.yml carries a `paths:` filter, so a pull
  // request that misses `src/**` never triggers it, the rollup never contains it, and green means
  // "it did not run". That is the `checks_exist` failure mode one level up — a check nobody ran
  // reads exactly like a check that passed.
  if (IS_PLATFORM) {
    if (conformanceRequired) {
      const conformance = checks.filter((c) => CONFORMANCE_CHECK.test(c.name || c.context || ''));
      const notGreen = conformance.filter((c) => state(c) !== 'SUCCESS');

      if (conformance.length === 0) {
        fail.push(
          'consumers_green: no consumer-conformance check ran on this platform-surface PR — a skipped ' +
            'conformance run is a red gate, not a missing one'
        );
      } else if (notGreen.length) {
        fail.push(
          `consumers_green: ${notGreen.map((c) => `${c.name || c.context} (${state(c) || 'no conclusion'})`).join(', ')} ` +
            '— a registered consumer does not build or does not pass against this change'
        );
      }
    }

    // Terraform is path-filtered and therefore cannot be globally required without deadlocking
    // every non-infra PR. It is still deterministic and blocking whenever `infra/**` changes.
    if (infraRequired) {
      const terraform = checks.filter((check) =>
        TERRAFORM_CHECK.test(`${check.workflowName ?? ''} ${check.name || check.context || ''}`));
      const notGreen = terraform.filter((check) => state(check) !== 'SUCCESS');
      if (terraform.length === 0) {
        fail.push('infra_green: no Terraform PR Check ran on this infra change');
      } else if (notGreen.length) {
        fail.push(`infra_green: ${notGreen.map((check) => `${context(check)} (${state(check) || 'no conclusion'})`).join(', ')} not passing`);
      }
    }

    const surface = SURFACE_RE.exec(pr.body ?? '');
    if (!surface) {
      fail.push(
        'surface_declared: the body has no "Surface: additive|breaking|none" line — an ' +
          'unclassified break gets announced without migration steps, which starts N agents down ' +
          'an unverified path'
      );
    } else if (surface[1].toLowerCase() === 'breaking' && !agentApproved) {
      fail.push(
        'surface_declared: "Surface: breaking" needs a live `agent:approved` verdict — the agent ' +
          'must verify the migration evidence before every consumer is told to follow it'
      );
    }
  }

  return { pr, fail, changeClass, stale };
}

const results = prs.map(evaluate).sort((a, b) => a.pr.number - b.pr.number);

// ── Close what the merge was supposed to close ───────────────────────────────
// GitHub auto-closes a linked issue AS THE MERGING ACTOR. `agent-merge.yml` merges with
// `GITHUB_TOKEN`, so without `issues: write` that close is silently dropped: the pull request
// closes, the issue stays open, and the board never drains.
//
// Observed on networthy over eight days, matched pairs differing only in who merged — every merge
// by a user token closed its issue on the merge timestamp; every `github-actions[bot]` merge left
// it open (#180 → #150 never closed at all; #177 → #172 and #171 → #149 were closed by hand hours
// later). An unattended loop then re-reads a board still advertising work that is already merged.
//
// The permission is granted now, but relying on the implicit behaviour is exactly what failed
// quietly for a week — so close them here, where the run log says whether it happened.
const linkedIssues = (pr) =>
  (pr.closingIssuesReferences ?? [])
    .map((i) => {
      const number = i?.number;
      const owner = i?.repository?.owner?.login;
      const name = i?.repository?.name;
      return Number.isInteger(number)
        ? { number, repository: owner && name ? `${owner}/${name}` : null }
        : null;
    })
    .filter(Boolean);

const issueReference = (issue) =>
  issue.repository ? `${issue.repository}#${issue.number}` : `#${issue.number} (repository unknown)`;

function closeLinkedIssues(pr) {
  for (const issue of linkedIssues(pr)) {
    // Closing by bare number uses the platform repo inferred from cwd. A linked issue may belong
    // to another repository, where the same number names unrelated work, so refuse incomplete
    // metadata and always tell `gh` exactly which repository owns the issue.
    if (!issue.repository) {
      console.log(`         WARN could not close ${issueReference(issue)}: no repository was returned by GitHub`);
      continue;
    }
    const { ok, out } = ghSoft(['issue', 'close', String(issue.number), '--repo', issue.repository,
      '--reason', 'completed', '--comment', `Closed by #${pr.number}.`]);
    // An issue already closed — by the implicit behaviour, or by a human ahead of the tick — is
    // the goal state, not a failure. Report either way; never let bookkeeping stop the loop.
    console.log(ok
      ? `         closed ${issueReference(issue)}`
      : `         WARN could not close ${issueReference(issue)}: ${out}`);
  }
}

function simulateClosingLinkedIssues(pr) {
  for (const issue of linkedIssues(pr)) {
    if (!issue.repository) {
      console.log(`         WOULD NOT CLOSE ${issueReference(issue)}: no repository was returned by GitHub`);
      continue;
    }
    console.log(`         WOULD CLOSE ${issueReference(issue)} with --repo ${issue.repository}`);
  }
}

// ── Report, then act ─────────────────────────────────────────────────────────
console.log(`autonomy level ${LEVEL} · ${results.length} open PR(s) · cap ${MAX_MERGES}/run\n`);

let merged = 0;
let updated = 0;
const DAY_MS = 86_400_000;
const ageDays = (pr) => {
  const t = Date.parse(pr.createdAt ?? '');
  return Number.isFinite(t) ? Math.floor((Date.now() - t) / DAY_MS) : null;
};
// The repo's own weekly review question is "open PRs older than two days — is review the
// constraint, or is a gate stuck?". Nothing ever answered it, because nothing printed an age.
const STALE_QUEUE_DAYS = 2;

for (const { pr, fail, changeClass, stale } of results) {
  // Printed for every pull request, on the dry run too, so "this will close nothing" is visible
  // BEFORE the merge rather than inferred from a board that stopped draining. Deliberately not a
  // gate: a `chore/` PR with no issue behind it is legitimate, and a gate that blocks on the
  // absence of a link would stall the queue on exactly the PRs nobody filed an issue for.
  const closes = linkedIssues(pr);
  const closesNote = closes.length
    ? `closes ${closes.map(issueReference).join(', ')}`
    : 'closes nothing — no issue is linked to this pull request';

  // ── Stale but otherwise clean: repair it, do not merge it ──────────────────
  // Deliberately gated on `fail.length === 0` — a branch is only worth updating when freshness is
  // the LAST thing wrong with it. Updating every BEHIND pull request regardless would re-trigger
  // CI on branches that are also unreviewed, unapproved or red, spending a full check run per
  // fifteen-minute tick to learn nothing.
  //
  // And it updates WITHOUT merging, even though the gates all passed a moment ago. The update
  // writes a new head commit, so every one of those checks now refers to a base that no longer
  // exists; merging on them is precisely the superseded-green failure the rollup logic above
  // exists to prevent. The next tick sees a fresh, green, CLEAN pull request and merges it then.
  if (fail.length === 0 && stale) {
    if (!DO_MERGE) {
      console.log(`  STALE  #${pr.number} ${pr.title} [${changeClass}] — passes every gate but is behind ${pr.baseRefName ?? 'the base branch'}; --merge would update it`);
      console.log(`         ${closesNote}`);
    } else if (updated >= MAX_MERGES) {
      console.log(`  HELD   #${pr.number} — under_cap: ${MAX_MERGES} branch update(s) already this run`);
    } else if (SIMULATE) {
      updated++;
      console.log(`  WOULD UPDATE #${pr.number} ${pr.title} — behind ${pr.baseRefName ?? 'base'}`);
    } else {
      const { ok, out } = ghSoft(['pr', 'update-branch', String(pr.number)]);
      updated++;
      console.log(ok
        ? `  UPDATE #${pr.number} ${pr.title} — updated from ${pr.baseRefName ?? 'base'}; merges next tick once checks are green`
        : `  BLOCK  #${pr.number} — mergeable: behind ${pr.baseRefName ?? 'base'} and the update failed: ${out}`);
    }
    continue;
  }

  if (fail.length === 0) {
    if (!DO_MERGE) {
      console.log(`  READY  #${pr.number} ${pr.title} [${changeClass}]`);
      console.log(`         ${closesNote}`);
    } else if (merged >= MAX_MERGES) {
      console.log(`  HELD   #${pr.number} — under_cap: ${MAX_MERGES} already merged this run`);
    } else {
      if (SIMULATE) {
        merged++;
        console.log(`  WOULD MERGE #${pr.number} ${pr.title} [${changeClass}]`);
        console.log(`         ${closesNote}`);
        simulateClosingLinkedIssues(pr);
        continue;
      }

      // The queue snapshot above is for reporting. Re-read every gate immediately before every
      // mutation, including the first merge, then make GitHub reject the operation if the author
      // moves the head between this evaluation and the merge API call.
      console.log(`  VERIFY #${pr.number} — re-reading every gate immediately before merge`);
      const fresh = evaluate(JSON.parse(gh(['pr', 'view', String(pr.number), '--json', PR_FIELDS])));
      if (fresh.fail.length) {
        console.log(`  BLOCK  #${pr.number} ${pr.title} [${changeClass}] — verdict changed during this run`);
        for (const failure of fresh.fail) console.log(`         - ${failure}`);
        continue;
      }
      if (fresh.stale) {
        const { ok, out } = ghSoft(['pr', 'update-branch', String(pr.number)]);
        updated++;
        console.log(ok
          ? `  UPDATE #${pr.number} ${pr.title} — base moved; updated, merges next tick after fresh checks`
          : `  BLOCK  #${pr.number} — behind after re-check and the update failed: ${out}`);
        continue;
      }

      gh(['pr', 'merge', String(pr.number), '--squash', '--delete-branch',
        '--match-head-commit', fresh.pr.headRefOid]);
      merged++;
      console.log(`  MERGED #${pr.number} ${pr.title} [${changeClass}]`);
      console.log(`         ${closesNote}`);
      closeLinkedIssues(pr);
    }
  } else {
    const age = ageDays(pr);
    const ageNote = age === null ? '' : ` · open ${age}d`;
    console.log(`  BLOCK  #${pr.number} ${pr.title} [${changeClass}]${ageNote}`);
    console.log(`         ${closesNote}`);
    for (const f of fail) console.log(`         - ${f}`);
  }
}

const blocked = results.filter((r) => r.fail.length);
const staleQueue = blocked.filter((r) => (ageDays(r.pr) ?? 0) >= STALE_QUEUE_DAYS);
console.log(`\n${results.length - blocked.length} ready · ${blocked.length} blocked · ${updated} updated · ${merged} merged\n`);

// ── Say out loud when the queue has stopped ──────────────────────────────────
// This run exits 0 whatever it finds (see below), which means a queue that has been frozen for a
// week and a queue that merged everything both render as a green checkmark on the schedule. That
// is not a hypothetical: the fleet ran this cron successfully every fifteen minutes while merging
// nothing at all, and the failure was invisible for weeks precisely because every run said
// "success". An annotation costs nothing and shows up on the run without failing it.
if (staleQueue.length) {
  const worst = Math.max(...staleQueue.map((r) => ageDays(r.pr) ?? 0));
  const msg =
    `${staleQueue.length} pull request(s) blocked for ${STALE_QUEUE_DAYS}+ days (oldest ${worst}d): ` +
    `${staleQueue.map((r) => `#${r.pr.number}`).join(', ')}. ` +
    'Review is either the constraint or a gate is stuck — a queue this old is not waiting for CI.';
  console.log(`  !! ${msg}\n`);
  if (process.env.GITHUB_ACTIONS) console.log(`::warning title=Merge queue is not moving::${msg}`);
}

// Blocked PRs are the normal state of a healthy queue. A broken policy read is not: if the merger
// cannot discover which checks are required, a green scheduled run would falsely claim the control
// loop is healthy while it is incapable of merging anything.
if (infrastructureFailures.size) {
  for (const failure of infrastructureFailures) {
    if (process.env.GITHUB_ACTIONS) console.log(`::error title=Merge gate infrastructure failure::${failure}`);
  }
  process.exit(1);
}
process.exit(0);
