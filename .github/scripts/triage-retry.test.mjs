#!/usr/bin/env node
// Deterministic policy test for triage-retry.mjs. No network, dispatch, or rerun occurs.

import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const retry = join(here, 'triage-retry.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'triage-retry-'));
const fixture = join(scratch, 'fixture.json');
const platformTitle = (number) => 'Triage platform request v2 #' + number;
const marketplaceTitle = (number) => 'Triage harness gap v2 #' + number;
const workflowRun = (title, number, overrides = {}) => ({
  databaseId: 5000 + number,
  displayTitle: title(number),
  status: 'completed',
  conclusion: 'failure',
  attempt: 1,
  createdAt: '2026-08-10T12:00:00Z',
  updatedAt: '2026-08-10T12:30:00Z',
  ...overrides,
});
const platformRun = (number, overrides = {}) => workflowRun(platformTitle, number, overrides);
const marketplaceRun = (number, overrides = {}) => workflowRun(marketplaceTitle, number, overrides);

writeFileSync(fixture, JSON.stringify({
  now: '2026-08-10T16:00:00Z',
  activeWorkflows: [
    '.github/workflows/platform-request-triage.lock.yml',
    '.github/workflows/marketplace-harness-gap-triage.lock.yml',
  ],
  issuesByLabel: {
    'platform-request': [
      { number: 1, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 2, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 3, updatedAt: '2026-08-10T15:50:00Z', labels: ['platform-request'] },
      { number: 4, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 5, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request', 'triage:accepted'] },
      { number: 6, lastEditedAt: '2026-08-10T11:00:00Z', updatedAt: '2026-08-10T14:00:00Z', labels: ['platform-request', 'triage:needs-info'] },
      { number: 7, lastEditedAt: '2026-08-10T15:00:00Z', updatedAt: '2026-08-10T15:00:00Z', labels: ['platform-request', 'triage:needs-info'] },
      { number: 8, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 9, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 10, updatedAt: '2026-08-10T12:00:00Z', labels: ['bug'] },
      { number: 11, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request', 'needs-human'] },
      { number: 12, lastEditedAt: '2026-08-10T15:00:00Z', updatedAt: '2026-08-10T15:00:00Z', labels: ['platform-request', 'triage:needs-info'] },
      { number: 13, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request', 'human-hold'] },
      { number: 14, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request', 'agent:blocked'] },
      { number: 15, lastEditedAt: '2026-08-10T14:00:00Z', updatedAt: '2026-08-10T14:00:00Z', labels: ['platform-request', 'triage:needs-info'] },
      { number: 16, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 17, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 18, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
      { number: 19, updatedAt: '2026-08-10T12:00:00Z', labels: ['platform-request'] },
    ],
    'harness-gap': [
      { number: 101, updatedAt: '2026-08-10T12:00:00Z', labels: ['harness-gap'] },
      { number: 102, updatedAt: '2026-08-10T12:00:00Z', labels: ['harness-gap'] },
      { number: 103, updatedAt: '2026-08-10T12:00:00Z', labels: ['harness-gap'] },
    ],
  },
  runsByWorkflow: {
    'platform-request-triage.lock.yml': [
      platformRun(2),
      platformRun(3, { updatedAt: '2026-08-10T15:50:00Z' }),
      platformRun(4, { attempt: 6, updatedAt: '2026-08-10T08:00:00Z' }),
      platformRun(6, { conclusion: 'success', updatedAt: '2026-08-10T14:00:00Z' }),
      platformRun(7, { conclusion: 'success', updatedAt: '2026-08-10T14:00:00Z' }),
      platformRun(8, { status: 'in_progress', conclusion: null, updatedAt: '2026-08-10T15:55:00Z' }),
      platformRun(9, { conclusion: 'success' }),
      platformRun(12, { updatedAt: '2026-08-10T15:30:00Z' }),
      platformRun(15, { conclusion: 'success', updatedAt: '2026-08-10T15:30:00Z' }),
      platformRun(16, { createdAt: '2026-07-12T16:00:00Z', updatedAt: '2026-08-10T08:00:00Z' }),
      platformRun(17, { attempt: 50, updatedAt: '2026-08-10T08:00:00Z' }),
      platformRun(18, { createdAt: '2026-07-12T16:01:00Z', updatedAt: '2026-08-10T08:00:00Z' }),
      platformRun(19, { attempt: 49, updatedAt: '2026-08-10T08:00:00Z' }),
    ],
    'marketplace-harness-gap-triage.lock.yml': [
      marketplaceRun(102, { updatedAt: '2026-08-10T08:00:00Z' }),
      platformRun(103, { updatedAt: '2026-08-10T08:00:00Z' }),
    ],
  },
}));

const result = spawnSync(process.execPath, [
  retry,
  '--fixture', fixture,
  '--recover',
  '--max-actions', '50',
], { encoding: 'utf8' });
const output = String(result.stdout) + String(result.stderr);
const expected = [
  [/WOULD DISPATCH #1\b/, 'an eligible issue with no current-policy run is bootstrapped'],
  [/WOULD RERUN #2\b/, 'an aged failed run is re-run'],
  [/WAIT #3\b.*retries after 30m/i, 'a recent failure respects backoff'],
  [/WOULD RERUN #4\b/, 'later attempts keep recovering at the capped backoff'],
  [/SKIP #5\b.*triage:accepted/i, 'a final verdict is never overwritten'],
  [/WAIT #6\b.*needs info/i, 'needs-info waits for a body edit'],
  [/WOULD DISPATCH #7\b.*body changed/i, 'an updated needs-info issue gets a fresh run'],
  [/WAIT #8\b.*in_progress/i, 'an active run is never duplicated'],
  [/WOULD RERUN #9\b/, 'a successful no-output run is not mistaken for a verdict'],
  [/SKIP #10\b.*target label/i, 'an unrelated issue never consumes capacity'],
  [/SKIP #11\b.*explicit hold needs-human/i, 'needs-human is never revived'],
  [/WOULD RERUN #12\b/, 'a failed needs-info re-entry follows normal retry policy'],
  [/SKIP #13\b.*explicit hold human-hold/i, 'human-hold is never revived'],
  [/SKIP #14\b.*explicit hold agent:blocked/i, 'agent:blocked is never revived'],
  [/WOULD DISPATCH #15\b.*body changed/i, 'an edit during a successful run gets a fresh reading'],
  [/WOULD DISPATCH #16\b.*29 days old/i, 'a run at the age boundary is renewed by dispatch'],
  [/WOULD DISPATCH #17\b.*rerun limit/i, 'attempt 50 is renewed by dispatch'],
  [/WOULD RERUN #18\b/, 'a run just under 29 days can still be re-run'],
  [/WOULD RERUN #19\b/, 'attempt 49 can still be re-run'],
  [/WOULD DISPATCH #101\b/, 'a harness-gap issue with no run dispatches its marketplace workflow'],
  [/WOULD RERUN #102\b/, 'the marketplace v2 title resolves its existing run'],
  [/WOULD DISPATCH #103\b/, 'a platform title cannot masquerade as a marketplace run'],
];

let failed = 0;
if (result.status !== 0) {
  console.log('  FAIL — recovery policy exited ' + result.status + '\n' + output);
  failed++;
}
for (const [pattern, why] of expected) {
  if (pattern.test(output)) {
    console.log('  ok   ' + why);
  } else {
    console.log('  FAIL — expected ' + pattern + ': ' + why + '\n' + output);
    failed++;
  }
}

const cappedResult = spawnSync(process.execPath, [
  retry,
  '--fixture', fixture,
  '--recover',
  '--max-actions', '2',
], { encoding: 'utf8' });
const cappedOutput = String(cappedResult.stdout) + String(cappedResult.stderr);
const queuedAtCap = cappedOutput.match(/^WOULD (?:DISPATCH|RERUN) /gm) ?? [];
if (
  cappedResult.status === 0 &&
  queuedAtCap.length === 2 &&
  /WAIT #4\b.*max-actions=2 reached/i.test(cappedOutput)
) {
  console.log('  ok   max-actions limits a scheduler tick without hiding later eligible work');
} else {
  console.log('  FAIL — max-actions did not stop after exactly two queued recoveries\n' + cappedOutput);
  failed++;
}

let repoRoot = process.env.TRIAGE_TEST_REPO_ROOT || here;
if (!process.env.TRIAGE_TEST_REPO_ROOT) {
  while (!existsSync(join(repoRoot, '.git')) && dirname(repoRoot) !== repoRoot) {
    repoRoot = dirname(repoRoot);
  }
}
const workflowSourcePaths = [...new Set([
  join(repoRoot, '.github', 'workflows', 'platform-request-triage.md'),
  join(repoRoot, '.github', 'workflows', 'marketplace-harness-gap-triage.md'),
  join(repoRoot, 'plugins', 'harness', 'skills', 'install-github-agentic-workflows', 'assets', 'platform-request-triage.md'),
  join(repoRoot, 'plugins', 'harness', 'skills', 'install-github-agentic-workflows', 'assets', 'marketplace-harness-gap-triage.md'),
])].filter(existsSync);

const extractGuard = (source) => {
  const frontmatter = source.split(/^---\s*$/m)[1] ?? '';
  const lines = frontmatter.split(/\r?\n/);
  const start = lines.findIndex((line) => line.startsWith('if:'));
  if (start === -1) return '';
  const parts = [lines[start].replace(/^if:\s*(?:>-?|\|-?)?\s*/, '')].filter(Boolean);
  for (let index = start + 1; index < lines.length; index++) {
    if (/^[A-Za-z][A-Za-z0-9-]*:/.test(lines[index])) break;
    if (lines[index].trim()) parts.push(lines[index].trim());
  }
  return parts.join(' ');
};
const contains = (haystack, needle) => Array.isArray(haystack)
  ? haystack.some((item) => String(item).toLowerCase() === String(needle).toLowerCase())
  : String(haystack).toLowerCase().includes(String(needle).toLowerCase());
const evaluateGuard = (guard, event) => {
  const javascript = guard.replaceAll(
    'github.event.issue.labels.*.name',
    'github.event.issue.labelNames'
  );
  const github = {
    event_name: event.eventName ?? 'issues',
    event: {
      action: event.action ?? '',
      issue: {
        state: event.state ?? 'open',
        labelNames: event.labels ?? [],
      },
      label: { name: event.labelName ?? '' },
    },
  };
  return Boolean(Function(
    'github', 'contains', 'toJSON',
    'return (' + javascript + ');'
  )(github, contains, JSON.stringify));
};

if (!workflowSourcePaths.length) {
  console.log('  ok   no platform/marketplace triage source is installed; source contract is not applicable');
}
for (const workflowPath of workflowSourcePaths) {
  const source = readFileSync(workflowPath, 'utf8');
  const frontmatter = source.split(/^---\s*$/m)[1] ?? '';
  const marketplace = workflowPath.endsWith('marketplace-harness-gap-triage.md');
  const target = marketplace ? 'harness-gap' : 'platform-request';
  const expectedTitle = marketplace ? 'Triage harness gap v2 #' : 'Triage platform request v2 #';
  const expectedMarker = marketplace
    ? '<!-- agent-triage workflow=harness-gap-v2 issue=${{ github.event.issue.number || inputs.issue_number }} run=${{ github.run_id }} -->'
    : '<!-- agent-triage workflow=platform-request-v2 issue=${{ github.event.issue.number || inputs.issue_number }} run=${{ github.run_id }} -->';
  const guard = extractGuard(source);
  const dispatchInput = /workflow_dispatch:\s*[\s\S]*?inputs:\s*[\s\S]*?issue_number:\s*[\s\S]*?required:\s*true\s*[\s\S]*?type:\s*string/.test(frontmatter);
  const exactTarget = 'target: "${{ github.event.issue.number || inputs.issue_number }}"';
  const exactTargetCount = frontmatter.split(exactTarget).length - 1;
  const dispatchCorrelation = dispatchInput &&
    frontmatter.includes('bots: [github-actions]') &&
    exactTargetCount >= 3;
  const cases = [
    ['target label applied', true, { action: 'labeled', labelName: target, labels: [target] }],
    ['unrelated issue labeled', false, { action: 'labeled', labelName: 'bug', labels: ['bug'] }],
    ['unrelated label on target issue', false, { action: 'labeled', labelName: 'bug', labels: [target] }],
    ['target issue reopened', true, { action: 'reopened', labels: [target] }],
    ['needs-info body edited', true, { action: 'edited', labels: [target, 'triage:needs-info'] }],
    ['ordinary body edit', false, { action: 'edited', labels: [target] }],
    ['closed issue', false, { action: 'reopened', state: 'closed', labels: [target] }],
    ['final verdict', false, { action: 'reopened', labels: [target, 'triage:accepted'] }],
    ['needs-human hold', false, { action: 'reopened', labels: [target, 'needs-human'] }],
    ['human-hold', false, { action: 'reopened', labels: [target, 'human-hold'] }],
    ['agent:blocked hold', false, { action: 'reopened', labels: [target, 'agent:blocked'] }],
    ['pinned recovery dispatch', true, { eventName: 'workflow_dispatch', state: 'closed' }],
  ];
  let sourceFailed = !source.includes('run-name: "' + expectedTitle) ||
    !source.includes(expectedMarker) ||
    !guard ||
    !dispatchCorrelation;
  for (const [name, expectedValue, event] of cases) {
    let actual;
    try {
      actual = evaluateGuard(guard, event);
    } catch {
      actual = 'guard-error';
    }
    if (actual !== expectedValue) {
      console.log('  FAIL — ' + target + ' event truth table: ' + name +
        ' expected ' + expectedValue + ', got ' + actual);
      sourceFailed = true;
    }
  }
  if (sourceFailed) {
    console.log('  FAIL — authored ' + target +
      ' trigger/run-name/dispatch-target policy is incomplete: ' + workflowPath);
    failed++;
  } else {
    console.log('  ok   authored ' + target +
      ' event truth table, v2 title, and exact dispatch/output correlation');
  }
}


const returnPathFiles = {
  deliver: join(repoRoot, 'plugins', 'plenipo', 'skills', 'deliver', 'SKILL.md'),
  fleet: join(repoRoot, 'plugins', 'plenipo', 'skills', 'fleet', 'SKILL.md'),
  platform: join(repoRoot, 'plugins', 'deliver', 'skills', 'request-platform-change', 'SKILL.md'),
  harness: join(repoRoot, 'plugins', 'harness', 'skills', 'report-harness-gap', 'SKILL.md'),
};
if (Object.values(returnPathFiles).every(existsSync)) {
  const returnPaths = Object.fromEntries(Object.entries(returnPathFiles)
    .map(([name, path]) => [name, readFileSync(path, 'utf8')]));
  const returnPathReady =
    returnPaths.deliver.includes('plenipo-request repo=<owner/name> issue=<n>') &&
    returnPaths.deliver.includes('harness-request repo=<owner/name> issue=<n>') &&
    returnPaths.deliver.includes('state`, `labels`, and `createdAt`') &&
    returnPaths.deliver.includes('needs-human`, `human-hold`, or `agent:blocked') &&
    returnPaths.deliver.includes('no other `triage:*` verdict') &&
    returnPaths.deliver.includes('/deliver:request-platform-change') &&
    returnPaths.deliver.includes('/harness:report-harness-gap') &&
    returnPaths.fleet.includes('triage:needs-info') &&
    returnPaths.fleet.includes('no `needs-human`, `human-hold`, or `agent:blocked`') &&
    returnPaths.fleet.includes('no other `triage:*` verdict') &&
    returnPaths.fleet.includes('| 0 |') &&
    returnPaths.platform.includes('edit the **existing issue body**') &&
    returnPaths.platform.includes('Plenipo.Core') &&
    returnPaths.platform.includes('.nuspec') &&
    returnPaths.platform.includes('plenipo-request repo=<platform-owner/repo> issue=<n>') &&
    returnPaths.platform.includes('github-actions[bot]') &&
    returnPaths.platform.includes('gh run view') &&
    returnPaths.platform.includes('Triage platform request v2 #<n>') &&
    returnPaths.platform.includes('untrusted') &&
    returnPaths.platform.includes('agent:blocked') &&
    returnPaths.harness.includes('**existing marketplace issue body**') &&
    returnPaths.harness.includes('skills.external[]') &&
    returnPaths.harness.includes('harness-request repo=<marketplace-owner/repo> issue=<n>') &&
    returnPaths.harness.includes('github-actions[bot]') &&
    returnPaths.harness.includes('gh run view') &&
    returnPaths.harness.includes('Triage harness gap v2 #<n>') &&
    returnPaths.harness.includes('untrusted') &&
    returnPaths.harness.includes('agent:blocked');
  if (returnPathReady) {
    console.log('  ok   local deliver/fleet ticks route needs-info back to both requester skills');
  } else {
    console.log('  FAIL — needs-info triage has no complete requester-side return path');
    failed++;
  }
}
const assetMerge = join(here, 'agent-merge.yml');
const installedMerge = join(here, '..', 'workflows', 'agent-merge.yml');
const mergeWorkflow = readFileSync(existsSync(assetMerge) ? assetMerge : installedMerge, 'utf8');
const recoveryJob = /  triage-recovery:([\s\S]*?)(?=\n  merge:)/.exec(mergeWorkflow)?.[1] ?? '';
if (
  recoveryJob.includes("if: vars.AGENT_TRIAGE_RECOVERY != 'off'") &&
  recoveryJob.includes('actions: write') &&
  recoveryJob.includes('run: node .github/scripts/triage-retry.mjs --recover') &&
  recoveryJob.includes('GH_TOKEN: $' + '{{ github.token }}')
) {
  console.log('  ok   scheduled recovery is independent and uses the Actions-write token');
} else {
  console.log('  FAIL — agent-merge does not run bounded triage recovery as an independent job');
  failed++;
}

if (!process.env.TRIAGE_NO_SOURCE_PROBE) {
  const ordinaryProductRoot = join(scratch, 'ordinary-product-without-triage');
  mkdirSync(ordinaryProductRoot, { recursive: true });
  const noSourceResult = spawnSync(process.execPath, [fileURLToPath(import.meta.url)], {
    encoding: 'utf8',
    env: {
      ...process.env,
      TRIAGE_NO_SOURCE_PROBE: '1',
      TRIAGE_TEST_REPO_ROOT: ordinaryProductRoot,
    },
  });
  const noSourceOutput = String(noSourceResult.stdout) + String(noSourceResult.stderr);
  if (
    noSourceResult.status === 0 &&
    /ok\s+no platform\/marketplace triage source is installed/i.test(noSourceOutput)
  ) {
    console.log('  ok   an ordinary product with no triage source treats source checks as a no-op');
  } else {
    console.log('  FAIL — no-source product probe did not terminate successfully\n' + noSourceOutput);
    failed++;
  }
}

if (failed) {
  console.log('\n' + failed + ' triage-retry case(s) wrong.\n');
  process.exit(1);
}
console.log('\nOK — bounded triage-recovery decisions, triggers, renewal, and scheduling behave correctly.\n');
