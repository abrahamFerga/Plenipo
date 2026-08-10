#!/usr/bin/env node
// Recover missing PR verdicts. Deterministic — it never approves or merges anything.
//
//   node .github/scripts/verdict-retry.mjs
//   node .github/scripts/verdict-retry.mjs --dispatch
//   node .github/scripts/verdict-retry.mjs --fixture retry.json --dispatch
//
// A transient model failure must not strand a PR forever, but retrying every scheduled merge tick
// burns capacity and can overwrite an in-flight judgement. Prefer re-running the original
// pull_request workflow: that preserves the PR event, actor and head SHA. A dispatch is only the
// bootstrap for a PR that has no run at all, and carries the exact head SHA in both its input and
// run name so it can never become an unbounded main-ref retry loop.

import { existsSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createApprovalProofClient, verdictTitle, VERDICT_WORKFLOW } from './approval-proof.mjs';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const index = argv.indexOf(name);
  return index === -1 ? undefined : argv[index + 1];
};

const FIXTURE = value('--fixture');
const DISPATCH = flag('--dispatch');
const RETRY_AFTER_MINUTES = Number(value('--retry-after-minutes') ?? 30);
const MAX_RETRY_AFTER_MINUTES = Number(value('--max-retry-after-minutes') ?? 360);
const LOOP_BRANCH = /^(feat|fix|chore)\//;
const HOLD_LABELS = ['human-hold', 'needs-human', 'agent:blocked'];

if (!Number.isFinite(RETRY_AFTER_MINUTES) || RETRY_AFTER_MINUTES < 1 ||
    !Number.isFinite(MAX_RETRY_AFTER_MINUTES) || MAX_RETRY_AFTER_MINUTES < RETRY_AFTER_MINUTES) {
  console.error('retry minutes must be positive and max-retry-after-minutes must be at least retry-after-minutes');
  process.exit(1);
}

const runGh = (args) => spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
const gh = (args) => {
  const result = runGh(args);
  if (result.error || result.status !== 0) {
    throw new Error(`gh ${args.join(' ')} failed:\n${result.error?.message || result.stderr || result.stdout}`);
  }
  return result.stdout;
};

const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const autonomy = cfg.autonomy ?? {};
const LEVEL = Number.isInteger(autonomy.level) ? autonomy.level : 0;
const MAX_REQUESTS = Number.isInteger(autonomy.maxVerdictRequestsPerTick)
  ? autonomy.maxVerdictRequestsPerTick
  : 2;

if (LEVEL < 1) {
  console.log(`autonomy level ${LEVEL}: no verdicts requested.`);
  process.exit(0);
}

if (!FIXTURE) {
  const workflows = JSON.parse(
    gh(['workflow', 'list', '--all', '--limit', '100', '--json', 'path,state'])
  );
  const expectedPath = `.github/workflows/${VERDICT_WORKFLOW}`.toLowerCase();
  const verdictWorkflow = workflows.find((workflow) => String(workflow.path).toLowerCase() === expectedPath);
  if (!verdictWorkflow || String(verdictWorkflow.state).toLowerCase() !== 'active') {
    console.error(`${VERDICT_WORKFLOW} is not installed and active — autonomy level ${LEVEL} has no approval authority.`);
    process.exit(1);
  }
}

let fixture = {};
let prs;
if (FIXTURE) {
  fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));
  prs = Array.isArray(fixture) ? fixture : fixture.pullRequests ?? [];
} else {
  prs = JSON.parse(
    gh([
      'pr',
      'list',
      '--state',
      'open',
      '--limit',
      '50',
      '--json',
      'number,body,isDraft,headRefName,headRefOid,baseRefName,labels',
    ])
  );
}

const now = FIXTURE && fixture.now ? new Date(fixture.now) : new Date();
if (Number.isNaN(now.valueOf())) {
  console.error('The fixture `now` value is not a valid timestamp');
  process.exit(1);
}

const labelsFor = (pr) => (pr.labels ?? []).map((label) => (typeof label === 'string' ? label : label.name).toLowerCase());

let workflowRuns;
const dispatchTitle = verdictTitle;
const approvalProof = FIXTURE ? null : createApprovalProofClient({ gh, runGh });

function latestAttempt(pr) {
  if (FIXTURE) return pr.lastVerdict ?? null;
  workflowRuns ??= JSON.parse(
    gh([
      'run',
      'list',
      '--workflow',
      VERDICT_WORKFLOW,
      '--limit',
      '100',
      '--json',
      'databaseId,headSha,createdAt,updatedAt,status,conclusion,event,attempt,displayTitle',
    ])
  );
  return workflowRuns
    .filter((run) => run.displayTitle === dispatchTitle(pr))
    .sort((left, right) =>
      Date.parse(right.updatedAt ?? right.createdAt) - Date.parse(left.updatedAt ?? left.createdAt))[0];
}

function shouldRequest(pr) {
  const labels = labelsFor(pr);
  if (!LOOP_BRANCH.test(pr.headRefName ?? '')) return { action: 'SKIP', why: 'not a loop branch' };
  if (!/plenipo-agent/.test(pr.body ?? '')) return { action: 'SKIP', why: 'no plenipo-agent envelope' };
  if (pr.isDraft) return { action: 'SKIP', why: 'draft' };
  if (labels.includes('agent:changes-requested')) return { action: 'SKIP', why: 'agent:changes-requested is still set' };
  for (const label of HOLD_LABELS) if (labels.includes(label)) return { action: 'SKIP', why: `${label} is set` };
  if (labels.includes('agent:approved')) {
    const proof = FIXTURE
      ? (pr.trustedApproval === false ? { ok: false } : { ok: true })
      : approvalProof.prove(pr);
    if (proof.ok) return { action: 'SKIP', why: 'current approval proof is already present' };
    console.log(`REPAIR #${pr.number} — agent:approved has no current proof; ${proof.why ?? 're-review required'}`);
  }

  const attempt = latestAttempt(pr);
  if (!attempt || attempt.displayTitle !== dispatchTitle(pr)) {
    return { action: 'DISPATCH', why: 'no current-policy verdict workflow exists for this head' };
  }

  if (String(attempt.status ?? 'completed').toLowerCase() !== 'completed') {
    return { action: 'WAIT', why: `same-head verdict is ${attempt.status ?? 'in progress'}` };
  }

  const lastActivity = Date.parse(attempt.updatedAt ?? attempt.createdAt);
  const ageMinutes = Number.isNaN(lastActivity) ? Infinity : (now.valueOf() - lastActivity) / 60_000;
  const attemptNumber = Math.max(1, Number(attempt.attempt) || 1);
  const retryAfter = Math.min(MAX_RETRY_AFTER_MINUTES, RETRY_AFTER_MINUTES * (2 ** (attemptNumber - 1)));
  if (ageMinutes < retryAfter) {
    return { action: 'WAIT', why: `same-head attempt ${attemptNumber} finished ${Math.max(0, Math.floor(ageMinutes))}m ago; retry after ${retryAfter}m` };
  }
  return { action: 'RERUN', why: `same-head attempt ${attemptNumber} has had no verdict for ${Math.floor(ageMinutes)}m`, attempt };
}

const candidates = [];
for (const pr of prs.sort((left, right) => left.number - right.number)) {
  const decision = shouldRequest(pr);
  if (!['DISPATCH', 'RERUN'].includes(decision.action)) {
    console.log(`${decision.action} #${pr.number} — ${decision.why}`);
    continue;
  }
  if (candidates.length >= MAX_REQUESTS) {
    console.log(`SKIP #${pr.number} — maxVerdictRequestsPerTick=${MAX_REQUESTS} reached`);
    continue;
  }
  candidates.push({ pr, decision });
  console.log(`WOULD ${decision.action} #${pr.number} — ${decision.why}`);
}

if (!DISPATCH || !candidates.length) {
  console.log(`\n${candidates.length} verdict recovery action(s) queued${DISPATCH ? '' : ' (dry run)'}.\n`);
  process.exit(0);
}

let defaultBranch = 'main';
if (!FIXTURE && candidates.some(({ decision }) => decision.action === 'DISPATCH')) {
  defaultBranch = JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef'])).defaultBranchRef.name;
}

let failed = 0;
for (const { pr, decision } of candidates) {
  if (FIXTURE) continue;
  try {
    if (decision.action === 'RERUN') {
      if (!decision.attempt?.databaseId) throw new Error('same-head verdict run has no databaseId');
      gh(['run', 'rerun', String(decision.attempt.databaseId)]);
      console.log(`RERAN #${pr.number} — workflow run ${decision.attempt.databaseId}`);
    } else {
      gh(['workflow', 'run', VERDICT_WORKFLOW, '--ref', defaultBranch,
        '-f', `pr_number=${pr.number}`, '-f', `pr_head_sha=${pr.headRefOid}`,
        '-f', `pr_base_ref=${pr.baseRefName}`]);
      console.log(`DISPATCHED #${pr.number} — ${VERDICT_WORKFLOW} for ${pr.headRefOid} from ${defaultBranch}`);
    }
  } catch (error) {
    console.error(`FAILED #${pr.number} — ${error.message}`);
    failed++;
  }
}

console.log(`\n${candidates.length} verdict recovery action(s) queued.\n`);
if (failed) process.exit(1);
