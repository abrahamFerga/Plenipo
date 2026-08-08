#!/usr/bin/env node
// Re-request missing PR verdicts. Deterministic — it never approves or merges anything.
//
//   node .github/scripts/verdict-retry.mjs
//   node .github/scripts/verdict-retry.mjs --dispatch
//   node .github/scripts/verdict-retry.mjs --fixture retry.json --dispatch
//
// A transient model failure must not strand a PR forever, but retrying every scheduled merge tick
// burns capacity and can overwrite an in-flight judgement. This dispatcher asks for at most the
// configured number of *missing* verdicts, waits for a same-head attempt to age out, and never
// retries an explicit negative or human-held result.

import { existsSync, readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const index = argv.indexOf(name);
  return index === -1 ? undefined : argv[index + 1];
};

const FIXTURE = value('--fixture');
const DISPATCH = flag('--dispatch');
const RETRY_AFTER_MINUTES = Number(value('--retry-after-minutes') ?? 30);
const LOOP_BRANCH = /^(feat|fix|chore)\//;
const HOLD_LABELS = ['human-hold', 'needs-human', 'agent:blocked'];
const VERDICT_WORKFLOW = 'pr-approval-verdict.lock.yml';

if (!Number.isFinite(RETRY_AFTER_MINUTES) || RETRY_AFTER_MINUTES < 1) {
  console.error('--retry-after-minutes must be a positive number');
  process.exit(1);
}

const gh = (args) => {
  const result = spawnSync('gh', args, { encoding: 'utf8', shell: process.platform === 'win32' });
  if (result.status !== 0) throw new Error(`gh ${args.join(' ')} failed:\n${result.stderr || result.stdout}`);
  return result.stdout;
};

const cfg = existsSync('workflow.json') ? JSON.parse(readFileSync('workflow.json', 'utf8')) : {};
const autonomy = cfg.autonomy ?? {};
const LEVEL = Number.isInteger(autonomy.level) ? autonomy.level : 0;
const MAX_REQUESTS = Number.isInteger(autonomy.maxVerdictRequestsPerTick)
  ? autonomy.maxVerdictRequestsPerTick
  : 2;

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
      'number,body,isDraft,headRefName,headRefOid,labels',
    ])
  );
}

const now = FIXTURE && fixture.now ? new Date(fixture.now) : new Date();
if (Number.isNaN(now.valueOf())) {
  console.error('The fixture `now` value is not a valid timestamp');
  process.exit(1);
}

const labelsFor = (pr) => (pr.labels ?? []).map((label) => (typeof label === 'string' ? label : label.name).toLowerCase());

function latestAttempt(pr) {
  if (FIXTURE) return pr.lastVerdict ?? null;
  const runs = JSON.parse(
    gh([
      'run',
      'list',
      '--workflow',
      VERDICT_WORKFLOW,
      '--branch',
      pr.headRefName,
      '--limit',
      '20',
      '--json',
      'headSha,createdAt,status,conclusion',
    ])
  );
  return runs
    .filter((run) => run.headSha === pr.headRefOid)
    .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))[0];
}

function shouldRequest(pr) {
  const labels = labelsFor(pr);
  if (!LOOP_BRANCH.test(pr.headRefName ?? '')) return { action: 'SKIP', why: 'not a loop branch' };
  if (!/plenipo-agent/.test(pr.body ?? '')) return { action: 'SKIP', why: 'no plenipo-agent envelope' };
  if (pr.isDraft) return { action: 'SKIP', why: 'draft' };
  if (labels.includes('agent:approved')) return { action: 'SKIP', why: 'agent:approved is already set' };
  if (labels.includes('agent:changes-requested')) return { action: 'SKIP', why: 'agent:changes-requested is still set' };
  for (const label of HOLD_LABELS) if (labels.includes(label)) return { action: 'SKIP', why: `${label} is set` };

  const attempt = latestAttempt(pr);
  if (!attempt || attempt.headSha !== pr.headRefOid) return { action: 'REQUEST', why: 'no verdict for this head' };

  const created = Date.parse(attempt.createdAt);
  const ageMinutes = Number.isNaN(created) ? Infinity : (now.valueOf() - created) / 60_000;
  if (ageMinutes < RETRY_AFTER_MINUTES) {
    return { action: 'WAIT', why: `same-head verdict started ${Math.max(0, Math.floor(ageMinutes))}m ago` };
  }
  return { action: 'REQUEST', why: `same-head verdict is ${Math.floor(ageMinutes)}m old` };
}

if (LEVEL < 1) {
  console.log(`autonomy level ${LEVEL}: no verdicts requested.`);
  process.exit(0);
}

const candidates = [];
for (const pr of prs.sort((left, right) => left.number - right.number)) {
  const decision = shouldRequest(pr);
  if (decision.action !== 'REQUEST') {
    console.log(`${decision.action} #${pr.number} — ${decision.why}`);
    continue;
  }
  if (candidates.length >= MAX_REQUESTS) {
    console.log(`SKIP #${pr.number} — maxVerdictRequestsPerTick=${MAX_REQUESTS} reached`);
    continue;
  }
  candidates.push(pr);
  console.log(`WOULD REQUEST #${pr.number} — ${decision.why}`);
}

if (!DISPATCH || !candidates.length) {
  console.log(`\n${candidates.length} verdict request(s) queued${DISPATCH ? '' : ' (dry run)'}.\n`);
  process.exit(0);
}

let defaultBranch = 'main';
if (!FIXTURE) {
  defaultBranch = JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef'])).defaultBranchRef.name;
}

let failed = 0;
for (const pr of candidates) {
  if (FIXTURE) continue;
  try {
    gh(['workflow', 'run', VERDICT_WORKFLOW, '--ref', defaultBranch, '-f', `pr_number=${pr.number}`]);
    console.log(`REQUESTED #${pr.number} — dispatched ${VERDICT_WORKFLOW} from ${defaultBranch}`);
  } catch (error) {
    console.error(`FAILED #${pr.number} — ${error.message}`);
    failed++;
  }
}

console.log(`\n${candidates.length} verdict request(s) queued.\n`);
if (failed) process.exit(1);
