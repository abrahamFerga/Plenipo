#!/usr/bin/env node
// Recover open issue-triage runs without turning provider failures into an unbounded loop.
//
//   node .github/scripts/triage-retry.mjs
//   node .github/scripts/triage-retry.mjs --recover
//   node .github/scripts/triage-retry.mjs --fixture retry.json --recover
//
// The issue event remains the primary trigger. This scheduler is only the bounded repair path:
// it bootstraps a missing current-policy run, re-runs a completed no-verdict attempt after
// exponential backoff, and dispatches a fresh run after an agent updates a needs-info issue.

import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const argv = process.argv.slice(2);
const flag = (name) => argv.includes(name);
const value = (name) => {
  const index = argv.indexOf(name);
  return index === -1 ? undefined : argv[index + 1];
};

const FIXTURE = value('--fixture');
const RECOVER = flag('--recover');
const RETRY_AFTER_MINUTES = Number(value('--retry-after-minutes') ?? 30);
const MAX_RETRY_AFTER_MINUTES = Number(value('--max-retry-after-minutes') ?? 360);
const MAX_ACTIONS = Number(value('--max-actions') ?? 2);
// GitHub refuses a rerun once the original run is 30 days old or has been rerun 50 times. Renew
// one day early so a scheduler tick cannot strand an otherwise recoverable issue at the boundary.
const MAX_RERUN_AGE_MINUTES = 29 * 24 * 60;
const MAX_RERUN_ATTEMPT = 50;

if (!Number.isFinite(RETRY_AFTER_MINUTES) || RETRY_AFTER_MINUTES < 1 ||
    !Number.isFinite(MAX_RETRY_AFTER_MINUTES) || MAX_RETRY_AFTER_MINUTES < RETRY_AFTER_MINUTES ||
    !Number.isInteger(MAX_ACTIONS) || MAX_ACTIONS < 1) {
  console.error('retry limits must be positive; max retry minutes must be at least the initial delay');
  process.exit(1);
}

const CONFIGS = [
  {
    workflow: 'platform-request-triage.lock.yml',
    targetLabel: 'platform-request',
    titlePrefix: 'Triage platform request v2 #',
  },
  {
    workflow: 'marketplace-harness-gap-triage.lock.yml',
    targetLabel: 'harness-gap',
    titlePrefix: 'Triage harness gap v2 #',
  },
];
const HOLD_LABELS = ['needs-human', 'human-hold', 'agent:blocked'];

const runGh = (args) => spawnSync('gh', args, {
  encoding: 'utf8',
  shell: false,
});
const gh = (args) => {
  const result = runGh(args);
  if (result.error || result.status !== 0) {
    throw new Error('gh ' + args.join(' ') + ' failed:\n' +
      (result.error?.message || result.stderr || result.stdout));
  }
  return result.stdout;
};

const fixture = FIXTURE ? JSON.parse(readFileSync(FIXTURE, 'utf8')) : {};
const now = FIXTURE && fixture.now ? new Date(fixture.now) : new Date();
if (Number.isNaN(now.valueOf())) {
  console.error('the fixture now value is not a valid timestamp');
  process.exit(1);
}

const workflowPaths = FIXTURE
  ? (fixture.activeWorkflows ?? [])
  : JSON.parse(gh(['workflow', 'list', '--all', '--limit', '100', '--json', 'path,state']))
    .filter((workflow) => String(workflow.state).toLowerCase() === 'active')
    .map((workflow) => workflow.path);

const active = CONFIGS.filter((config) => {
  const expected = ('.github/workflows/' + config.workflow).toLowerCase();
  return workflowPaths.some((path) => String(path).toLowerCase() === expected);
});

if (!active.length) {
  console.log('No active recoverable triage workflows.');
  process.exit(0);
}

const labelsFor = (issue) => (issue.labels ?? [])
  .map((label) => typeof label === 'string' ? label : label.name)
  .filter(Boolean)
  .map((label) => label.toLowerCase());

let repoIdentity;
const issuesFor = (config) => {
  if (FIXTURE) return fixture.issuesByLabel?.[config.targetLabel] ?? [];
  repoIdentity ??= JSON.parse(gh(['repo', 'view', '--json', 'nameWithOwner']))
    .nameWithOwner.split('/');
  const query = 'query($owner:String!,$name:String!,$label:String!){repository(owner:$owner,name:$name){issues(first:100,states:OPEN,labels:[$label],orderBy:{field:UPDATED_AT,direction:DESC}){nodes{number createdAt updatedAt lastEditedAt labels(first:50){nodes{name}}}}}}';
  const response = JSON.parse(gh([
    'api', 'graphql',
    '-f', 'query=' + query,
    '-F', 'owner=' + repoIdentity[0],
    '-F', 'name=' + repoIdentity[1],
    '-F', 'label=' + config.targetLabel,
  ]));
  return response.data.repository.issues.nodes.map((issue) => ({
    ...issue,
    labels: issue.labels.nodes,
  }));
};

const timestamp = (item) => Date.parse(item.updatedAt ?? item.createdAt ?? '');
const ageMinutes = (item) => {
  const then = timestamp(item);
  return Number.isNaN(then) ? Infinity : (now.valueOf() - then) / 60_000;
};

const retryDecision = (attempt) => {
  const attemptNumber = Math.max(1, Number(attempt.attempt) || 1);
  const retryAfter = Math.min(
    MAX_RETRY_AFTER_MINUTES,
    RETRY_AFTER_MINUTES * (2 ** (attemptNumber - 1))
  );
  if (ageMinutes(attempt) < retryAfter) {
    return {
      action: 'WAIT',
      why: 'attempt ' + attemptNumber + ' retries after ' + retryAfter + 'm',
    };
  }
  const originalRunCreatedAt = Date.parse(attempt.createdAt ?? '');
  const originalRunAgeMinutes = Number.isNaN(originalRunCreatedAt)
    ? Infinity
    : (now.valueOf() - originalRunCreatedAt) / 60_000;
  if (attemptNumber >= MAX_RERUN_ATTEMPT) {
    return {
      action: 'DISPATCH',
      why: 'attempt ' + attemptNumber + ' reached the rerun limit; renewing with a fresh run',
    };
  }
  if (originalRunAgeMinutes >= MAX_RERUN_AGE_MINUTES) {
    return {
      action: 'DISPATCH',
      why: 'original run is at least 29 days old; renewing with a fresh run',
    };
  }
  return {
    action: 'RERUN',
    why: 'attempt ' + attemptNumber + ' produced no final verdict',
    attempt,
  };
};

const candidates = [];

for (const config of active) {
  const issues = issuesFor(config);
  const runs = FIXTURE
    ? (fixture.runsByWorkflow?.[config.workflow] ?? [])
    : JSON.parse(gh([
      'run', 'list',
      '--workflow', config.workflow,
      '--limit', '100',
      '--json', 'databaseId,displayTitle,status,conclusion,attempt,createdAt,updatedAt',
    ]));

  for (const issue of issues.sort((left, right) => {
    const byCreated = Date.parse(left.createdAt ?? '') - Date.parse(right.createdAt ?? '');
    return Number.isNaN(byCreated) || byCreated === 0 ? left.number - right.number : byCreated;
  })) {
    const labels = labelsFor(issue);
    if (!labels.includes(config.targetLabel)) {
      console.log('SKIP #' + issue.number + ' — target label is absent');
      continue;
    }
    const hold = HOLD_LABELS.find((label) => labels.includes(label));
    if (hold) {
      console.log('SKIP #' + issue.number + ' — explicit hold ' + hold + ' is present');
      continue;
    }
    const terminal = labels.find((label) => label.startsWith('triage:') && label !== 'triage:needs-info');
    if (terminal) {
      console.log('SKIP #' + issue.number + ' — final verdict ' + terminal + ' is present');
      continue;
    }

    const title = config.titlePrefix + issue.number;
    const attempt = runs
      .filter((run) => run.displayTitle === title)
      .sort((left, right) => timestamp(right) - timestamp(left))[0];

    let decision;
    if (!attempt) {
      decision = { action: 'DISPATCH', why: 'no current-policy triage run exists' };
    } else if (String(attempt.status).toLowerCase() !== 'completed') {
      decision = { action: 'WAIT', why: 'latest triage run is ' + attempt.status };
    } else if (
      labels.includes('triage:needs-info') &&
      String(attempt.conclusion).toLowerCase() === 'success'
    ) {
      const issueChanged = Date.parse(issue.lastEditedAt ?? '') >
        Date.parse(attempt.createdAt ?? '');
      decision = issueChanged
        ? { action: 'DISPATCH', why: 'issue body changed after the needs-info verdict' }
        : { action: 'WAIT', why: 'needs info; an issue-body edit is the retry trigger' };
    } else {
      decision = retryDecision(attempt);
    }

    if (!['DISPATCH', 'RERUN'].includes(decision.action)) {
      console.log(decision.action + ' #' + issue.number + ' — ' + decision.why);
      continue;
    }
    if (candidates.length >= MAX_ACTIONS) {
      console.log('WAIT #' + issue.number + ' — max-actions=' + MAX_ACTIONS + ' reached');
      continue;
    }
    candidates.push({ config, issue, decision });
    console.log('WOULD ' + decision.action + ' #' + issue.number + ' — ' + decision.why);
  }
}

if (!RECOVER || !candidates.length) {
  console.log('\n' + candidates.length + ' triage recovery action(s) queued' +
    (RECOVER ? '.' : ' (dry run).') + '\n');
  process.exit(0);
}

let defaultBranch;
let failed = 0;
for (const { config, issue, decision } of candidates) {
  if (FIXTURE) continue;
  try {
    if (decision.action === 'RERUN') {
      if (!decision.attempt?.databaseId) throw new Error('triage run has no databaseId');
      gh(['run', 'rerun', String(decision.attempt.databaseId)]);
      console.log('RERAN #' + issue.number + ' — workflow run ' + decision.attempt.databaseId);
    } else {
      defaultBranch ??= JSON.parse(gh(['repo', 'view', '--json', 'defaultBranchRef']))
        .defaultBranchRef.name;
      gh([
        'workflow', 'run', config.workflow,
        '--ref', defaultBranch,
        '-f', 'issue_number=' + issue.number,
      ]);
      console.log('DISPATCHED #' + issue.number + ' — ' + config.workflow + ' from ' + defaultBranch);
    }
  } catch (error) {
    console.error('FAILED #' + issue.number + ' — ' + error.message);
    failed++;
  }
}

console.log('\n' + candidates.length + ' triage recovery action(s) queued.\n');
if (failed) process.exit(1);
