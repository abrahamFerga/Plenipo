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
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
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

// ── The linked issue must be named before the merge, not discovered after it ──
// A `GITHUB_TOKEN` merge without `issues: write` closes the pull request and silently leaves the
// issue open, so an unattended board keeps advertising merged work. That failure was invisible
// because nothing in the run log ever mentioned the issue. These assert on the dry run — the step
// `agent-merge.yml` always executes — so the intent is on record even when the merge is skipped.
//
// Asserts on the NOTE rather than on a close actually happening: closing needs `gh` and a live
// repo, which this file deliberately does not have.
const closeCases = [
  [906, /closes #150\b/, 'a linked issue must be named in the run log, or a silent no-close is invisible'],
  [907, /closes nothing/, 'a pull request that will close nothing must say so before it merges'],
];

for (const [number, mustMatch, why] of closeCases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
  }

  if (mustMatch.test(reasons)) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — expected ${mustMatch} in the report.\n       ${why}\n       reported:\n${reasons}`);
    failed++;
  }
}

// ── A stale branch is repairable, a conflicted one is not ────────────────────
// `BEHIND` used to sit in the same list as `DIRTY` and `BLOCKED`, and that one line froze the whole
// fleet: the first merge onto main made every other open pull request BEHIND, nothing ever ran
// `gh pr update-branch`, and so the queue absorbed exactly one merge and then stopped. Fourteen of
// twenty-five open PRs across six repos were stuck on this single reason.
//
// These assert on the presence of a `mergeable:` reason rather than on READY/STALE/BLOCK, for the
// same reason as everything above: a verdict depends on `autonomy.level`, a gate reason does not.
const mergeableCases = [
  [908, false, 'BEHIND must not block — it is staleness, and this script can repair it in one call'],
  [909, true, 'DIRTY must still block — a real conflict needs the author, not a branch update'],
];

for (const [number, mustFail, why] of mergeableCases) {
  const reasons = reasonsFor(number);
  if (reasons === null) {
    console.log(`  FAIL #${number} — not present in the gate's output at all`);
    failed++;
    continue;
  }

  const fired = /mergeable:/.test(reasons);
  if (fired === mustFail) {
    console.log(`  ok   #${number} — ${why}`);
  } else {
    console.log(`  FAIL #${number} — the mergeable gate ${fired ? 'fired' : 'did NOT fire'}, expected the opposite.\n       ${why}\n       reasons:\n${reasons}`);
    failed++;
  }
}

// ── Which stale branches actually get updated ────────────────────────────────
// The routing above is level-dependent by construction — a branch is only worth updating when
// freshness is the LAST thing wrong with it, and at level 0 nothing is. So this runs the gate in a
// scratch directory holding a level-3 `workflow.json`, which is the only way to assert the
// STALE-versus-BLOCK split deterministically. The gate reads policy from `workflow.json` in the
// working directory and the fixture path is absolute, so cwd is the whole control surface.
const scratch = mkdtempSync(join(tmpdir(), 'merge-gate-'));
writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3 } }));

const levelled = spawnSync(process.execPath, [gate, '--fixture', fixture], {
  encoding: 'utf8',
  cwd: scratch,
});

if (levelled.status !== 0) {
  console.log(`  FAIL — the gate exited ${levelled.status} under a level-3 policy\n${levelled.stderr || levelled.stdout}`);
  failed++;
} else {
  // [pr, the verdict its line must carry, what this case is protecting]
  const routing = [
    [908, 'STALE', 'a PR that passes every gate but freshness must be offered a branch update'],
    [909, 'BLOCK', 'a conflicted PR must never be routed to update-branch'],
    [910, 'BLOCK', 'behind AND unapproved must stay blocked — updating it spends a CI run to learn nothing'],
  ];

  for (const [number, verdict, why] of routing) {
    const line = levelled.stdout.split('\n').find((l) => l.includes(`#${number} `));
    if (line === undefined) {
      console.log(`  FAIL #${number} — not present in the level-3 output at all`);
      failed++;
    } else if (new RegExp(`^\\s{2}${verdict}\\b`).test(line)) {
      console.log(`  ok   #${number} — ${why}`);
    } else {
      console.log(`  FAIL #${number} — expected ${verdict}, got:\n       ${line.trim()}\n       ${why}`);
      failed++;
    }
  }
}

// ── `--fixture --merge` must never touch the network ─────────────────────────
// Fixture data describes pull requests numbered 901-910 that exist nowhere. If `--merge` did not
// degrade to a simulation, running this very test file with the merge flag would try to squash
// pull request #901 in whatever repo the runner happened to be sitting in — and on a product repo
// those numbers are real. The failure mode is not a wrong verdict, it is a wrong merge.
const simulated = spawnSync(process.execPath, [gate, '--fixture', fixture, '--merge'], {
  encoding: 'utf8',
  cwd: scratch,
});

if (simulated.status !== 0) {
  console.log(`  FAIL — \`--fixture --merge\` exited ${simulated.status}; it must simulate, not call gh\n${(simulated.stderr || simulated.stdout).split('\n').slice(0, 4).join('\n')}`);
  failed++;
} else if (!/WOULD (MERGE|UPDATE)/.test(simulated.stdout)) {
  console.log(`  FAIL — \`--fixture --merge\` produced no WOULD MERGE/UPDATE line, so nothing proves it simulated`);
  failed++;
} else if (/^\s{2}(MERGED|UPDATE) /m.test(simulated.stdout)) {
  console.log(`  FAIL — \`--fixture --merge\` reported a REAL merge or branch update on fixture data`);
  failed++;
} else {
  console.log('  ok   simulate — `--fixture --merge` simulates and never reaches the network');
}

if (failed) {
  console.log(`\n${failed} rollup case(s) wrong. merge-gate is the last automated thing before main — do not merge this.\n`);
  process.exit(1);
}
console.log(`\nOK — ${cases.length} rollup, ${closeCases.length} linked-issue, ${mergeableCases.length} mergeable, 3 stale-routing and 1 simulation case(s) behave correctly.\n`);
