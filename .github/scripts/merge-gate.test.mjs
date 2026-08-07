#!/usr/bin/env node
// Self-test for merge-gate.mjs's check-rollup handling. No network, no `gh`, no merging.
//
//   node .github/scripts/merge-gate.test.mjs
//
// `merge-gate.mjs` advertises `--fixture` as "used to test itself", and `brokenWorkflows` was
// deliberately factored out of its `gh` call so a fixture could prove it red before green. This is
// the missing half of that: the rollup path had no fixture at all, and an earlier attempt to fix a
// stale-check bug shipped a REGRESSION — it merged a pull request whose re-run was still queued —
// which a fixture would have caught in seconds and a live-queue A/B did not.
//
// Asserts on the GATE REASON rather than READY/BLOCK on purpose. A verdict depends on
// `autonomy.level`, so an assertion phrased as "must be READY" would break the day a human lowers
// the level — turning a real check into noise someone silences.

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const gate = join(here, 'merge-gate.mjs');
const fixture = join(here, 'fixtures', 'check-rollup.json');

const run = spawnSync(process.execPath, [gate, '--fixture', fixture], { encoding: 'utf8' });
if (run.status !== 0) {
  console.error(`merge-gate exited ${run.status}\n${run.stderr || run.stdout}`);
  process.exit(1);
}

const output = run.stdout;

// Each fixture PR's block is its "#<n>" line plus the indented reasons that follow it.
const reasonsFor = (number) => {
  const lines = output.split('\n');
  const start = lines.findIndex((l) => l.includes(`#${number} `));
  if (start === -1) return null;
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((l) => /^\s{2}(READY|BLOCK|HELD|MERGED)/.test(l));
  return (end === -1 ? rest : rest.slice(0, end)).join('\n');
};

// [pr, must the checks_green gate fire?, what this case is protecting]
const cases = [
  [901, false, 'a stale FAILURE superseded by a fresh SUCCESS must not block — the bug this path exists to fix'],
  [902, true, 'a QUEUED re-run must block: merging on a superseded green is unrecoverable'],
  [903, true, 'identical timestamps must not resolve by array position — prefer the worse conclusion'],
  [904, true, 'two workflows sharing a job name must not collapse into one verdict'],
  [905, true, 'legacy StatusContext entries have no startedAt; createdAt must still order them'],
];

let failed = 0;
for (const [number, mustFail, why] of cases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
    }

  const fired = /checks_green/.test(reasons);
  if (fired === mustFail) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — checks_green ${fired ? 'fired' : 'did NOT fire'}, expected the opposite.\n       ${why}\n       reasons:\n${reasons}`);
    failed++;
  }
}

if (failed) {
  console.log(`\n${failed} rollup case(s) wrong. merge-gate is the last automated thing before main — do not merge this.\n`);
  process.exit(1);
}
console.log(`\nOK — ${cases.length} rollup case(s) behave correctly.\n`);
