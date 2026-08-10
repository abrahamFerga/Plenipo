#!/usr/bin/env node
// Self-test for verdict-retry.mjs. No network, workflow dispatch or workflow re-run.
//
//   node .github/scripts/verdict-retry.test.mjs

import { spawnSync } from 'node:child_process';
import { chmodSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { delimiter, dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const retry = join(here, 'verdict-retry.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'verdict-retry-'));
const fixture = join(scratch, 'fixture.json');

writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3, maxVerdictRequestsPerTick: 4 } }));
writeFileSync(
  fixture,
  JSON.stringify({
    now: '2026-08-08T18:00:00Z',
    pullRequests: [
      {
        number: 1001,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/needs-retry',
        headRefOid: 'a'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2001,
          headSha: 'a'.repeat(40),
          createdAt: '2026-08-08T16:50:00Z',
          updatedAt: '2026-08-08T17:00:00Z',
          status: 'completed',
          attempt: 1,
          displayTitle: `Approval verdict PR #1001 @ ${'a'.repeat(40)} -> main`,
        },
      },
      {
        number: 1002,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/review-in-flight',
        headRefOid: 'b'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2002,
          headSha: 'b'.repeat(40),
          createdAt: '2026-08-08T17:45:00Z',
          updatedAt: '2026-08-08T17:50:00Z',
          status: 'completed',
          attempt: 1,
          displayTitle: `Approval verdict PR #1002 @ ${'b'.repeat(40)} -> main`,
        },
      },
      {
        number: 1003,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/held',
        headRefOid: 'c'.repeat(40),
        labels: [{ name: 'needs-human' }],
      },
      {
        number: 1004,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/already-reviewed',
        headRefOid: 'd'.repeat(40),
        labels: [{ name: 'agent:approved' }],
      },
      {
        number: 1005,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/new-head',
        headRefOid: 'e'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2005,
          headSha: 'f'.repeat(40),
          createdAt: '2026-08-08T17:59:00Z',
          updatedAt: '2026-08-08T17:59:00Z',
          status: 'completed',
          attempt: 1,
        },
      },
      {
        number: 1006,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'manual/branch',
        headRefOid: 'g'.repeat(40),
        labels: [],
      },
      {
        number: 1007,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/backoff',
        headRefOid: '1'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2007,
          headSha: '1'.repeat(40),
          createdAt: '2026-08-08T16:00:00Z',
          updatedAt: '2026-08-08T17:15:00Z',
          status: 'completed',
          attempt: 2,
          displayTitle: `Approval verdict PR #1007 @ ${'1'.repeat(40)} -> main`,
        },
      },
      {
        number: 1008,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/still-running',
        headRefOid: '2'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2008,
          headSha: '2'.repeat(40),
          createdAt: '2026-08-08T16:00:00Z',
          updatedAt: '2026-08-08T16:00:00Z',
          status: 'in_progress',
          attempt: 2,
          displayTitle: `Approval verdict PR #1008 @ ${'2'.repeat(40)} -> main`,
        },
      },
      {
        number: 1009,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/legacy-policy-run',
        headRefOid: '3'.repeat(40),
        labels: [],
        lastVerdict: {
          databaseId: 2009,
          headSha: '3'.repeat(40),
          createdAt: '2026-08-08T17:00:00Z',
          updatedAt: '2026-08-08T17:30:00Z',
          status: 'completed',
          attempt: 1,
          displayTitle: 'Approval verdict',
        },
      },
      {
        number: 1010,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/unproven-label',
        headRefOid: '4'.repeat(40),
        baseRefName: 'main',
        labels: [{ name: 'agent:approved' }],
        trustedApproval: false,
        lastVerdict: {
          databaseId: 2010,
          createdAt: '2026-08-08T16:00:00Z',
          updatedAt: '2026-08-08T16:30:00Z',
          status: 'completed',
          attempt: 1,
          displayTitle: `Approval verdict PR #1010 @ ${'4'.repeat(40)} -> main`,
        },
      },
    ],
  })
);

const run = spawnSync(process.execPath, [retry, '--fixture', fixture, '--dispatch'], {
  encoding: 'utf8',
  cwd: scratch,
});
const output = `${run.stdout}${run.stderr}`;
const expected = [
  [/WOULD RERUN #1001\b/, 'an expired same-head verdict re-runs its original PR event'],
  [/WAIT #1002\b/, 'an in-flight verdict is not duplicated'],
  [/SKIP #1003\b.*needs-human/i, 'an explicit human hold is never retried'],
  [/SKIP #1004\b.*approval proof/i, 'a proven current approval is never overwritten'],
  [/WOULD DISPATCH #1005\b/, 'a verdict for an old head never covers a new commit'],
  [/SKIP #1006\b.*loop branch/i, 'manual branches are outside the agent queue'],
  [/WAIT #1007\b.*retry after 60m/i, 'later attempts back off exponentially instead of firing every tick'],
  [/WAIT #1008\b.*in_progress/i, 'an active workflow is never duplicated even when it is old'],
  [/WOULD DISPATCH #1009\b/, 'a legacy same-head run cannot revive an obsolete reviewer policy'],
  [/WOULD RERUN #1010\b/, 'a free-floating approval label is repaired rather than trusted'],
];

let failed = 0;
if (run.status !== 0) {
  console.log(`  FAIL — retry dispatcher exited ${run.status}\n${output}`);
  failed++;
}
for (const [pattern, why] of expected) {
  if (pattern.test(output)) {
    console.log(`  ok   ${why}`);
  } else {
    console.log(`  FAIL — expected ${pattern}: ${why}\n${output}`);
    failed++;
  }
}

const optionalScratch = mkdtempSync(join(tmpdir(), 'verdict-retry-optional-'));
const optionalBin = join(optionalScratch, 'bin');
mkdirSync(optionalBin);
writeFileSync(join(optionalScratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3 } }));
const optionalGh = join(optionalBin, 'gh-mock.mjs');
writeFileSync(
  optionalGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'workflow' && args[1] === 'list') { console.log('[]'); process.exit(0); }\n` +
    `console.error('verdict recovery queried PRs even though no reviewer is installed');\n` +
    `process.exit(9);\n`
);
if (process.platform === 'win32') {
  writeFileSync(join(optionalBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(optionalBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}
const optional = spawnSync(process.execPath, [retry, '--dispatch'], {
  encoding: 'utf8',
  cwd: optionalScratch,
  env: { ...process.env, PATH: `${optionalBin}${delimiter}${process.env.PATH ?? ''}` },
});
const optionalOutput = `${optional.stdout}${optional.stderr}`;
if (optional.status !== 0 && /not installed and active.*no approval authority/i.test(optionalOutput)) {
  console.log('  ok   autonomous mode fails visibly when its approval authority is not installed');
} else {
  console.log(`  FAIL — missing approval authority looked healthy:\n${optionalOutput}`);
  failed++;
}

writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 0 } }));
const disabled = spawnSync(process.execPath, [retry, '--fixture', fixture, '--dispatch'], {
  encoding: 'utf8',
  cwd: scratch,
});
const disabledOutput = `${disabled.stdout}${disabled.stderr}`;
if (disabled.status === 0 && /autonomy level 0.*no verdicts requested/i.test(disabledOutput) && !/WOULD (?:RERUN|DISPATCH) #/.test(disabledOutput)) {
  console.log('  ok   level 0 never consumes model capacity or creates a verdict');
} else {
  console.log(`  FAIL — level 0 dispatched or did not explain its no-op:\n${disabledOutput}`);
  failed++;
}

const assetMergeWorkflow = join(here, 'agent-merge.yml');
const installedMergeWorkflow = join(here, '..', 'workflows', 'agent-merge.yml');
const mergeWorkflow = readFileSync(
  existsSync(assetMergeWorkflow) ? assetMergeWorkflow : installedMergeWorkflow,
  'utf8'
);
const recoveryStep = /- name: Recover missing approval verdicts([\s\S]*?)(?=\n\s+- name: Report verdict recovery failure)/
  .exec(mergeWorkflow)?.[1] ?? '';
const mergeStep = /- name: Merge([\s\S]*?)$/.exec(mergeWorkflow)?.[1] ?? '';
if (
  recoveryStep.includes('GH_TOKEN: ${{ github.token }}') &&
  mergeStep.includes('GH_TOKEN: ${{ secrets.COPILOT_GITHUB_TOKEN || github.token }}')
) {
  console.log('  ok   verdict dispatch uses scoped Actions write while PR mutations retain the event-producing PAT');
} else {
  console.log('  FAIL — verdict dispatch and PR mutations do not use their least-privilege token split');
  failed++;
}

if (failed) {
  console.log(`\n${failed} verdict-retry case(s) wrong. Recovery must be bounded and must not revive a held PR.\n`);
  process.exit(1);
}

console.log(`\nOK — ${expected.length + 3} verdict-retry policy case(s) behave correctly.\n`);
