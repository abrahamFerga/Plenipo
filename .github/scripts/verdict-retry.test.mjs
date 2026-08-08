#!/usr/bin/env node
// Self-test for verdict-retry.mjs. No network and no workflow dispatch.
//
//   node .github/scripts/verdict-retry.test.mjs

import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const retry = join(here, 'verdict-retry.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'verdict-retry-'));
const fixture = join(scratch, 'fixture.json');

writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3, maxVerdictRequestsPerTick: 2 } }));
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
        lastVerdict: { headSha: 'a'.repeat(40), createdAt: '2026-08-08T17:00:00Z' },
      },
      {
        number: 1002,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'fix/review-in-flight',
        headRefOid: 'b'.repeat(40),
        labels: [],
        lastVerdict: { headSha: 'b'.repeat(40), createdAt: '2026-08-08T17:50:00Z' },
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
        lastVerdict: { headSha: 'f'.repeat(40), createdAt: '2026-08-08T17:59:00Z' },
      },
      {
        number: 1006,
        body: 'plenipo-agent envelope',
        isDraft: false,
        headRefName: 'manual/branch',
        headRefOid: 'g'.repeat(40),
        labels: [],
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
  [/WOULD REQUEST #1001\b/, 'a failed or expired verdict is retried automatically'],
  [/WAIT #1002\b/, 'an in-flight verdict is not duplicated'],
  [/SKIP #1003\b.*needs-human/i, 'an explicit human hold is never retried'],
  [/SKIP #1004\b.*agent:approved/i, 'an existing verdict is never overwritten'],
  [/WOULD REQUEST #1005\b/, 'a verdict for an old head never covers a new commit'],
  [/SKIP #1006\b.*loop branch/i, 'manual branches are outside the agent queue'],
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

writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 0 } }));
const disabled = spawnSync(process.execPath, [retry, '--fixture', fixture, '--dispatch'], {
  encoding: 'utf8',
  cwd: scratch,
});
const disabledOutput = `${disabled.stdout}${disabled.stderr}`;
if (disabled.status === 0 && /autonomy level 0.*no verdicts requested/i.test(disabledOutput) && !/REQUEST #/.test(disabledOutput)) {
  console.log('  ok   level 0 never consumes model capacity or creates a verdict');
} else {
  console.log(`  FAIL — level 0 dispatched or did not explain its no-op:\n${disabledOutput}`);
  failed++;
}

if (failed) {
  console.log(`\n${failed} verdict-retry case(s) wrong. A retry must be bounded and must not revive a held PR.\n`);
  process.exit(1);
}

console.log(`\nOK — ${expected.length + 1} verdict-retry policy case(s) behave correctly.\n`);
