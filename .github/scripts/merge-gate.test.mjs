#!/usr/bin/env node
// Self-test for merge-gate.mjs's check-rollup handling. No network, no `gh`, no merging.
//
//   node .github/scripts/merge-gate.test.mjs
//
// `merge-gate.mjs` advertises `--fixture` as "used to test itself". An earlier attempt to fix a
// stale-check bug shipped a REGRESSION — it merged a pull request whose re-run was still queued —
// which a fixture catches in seconds and a live-queue A/B did not.
//
// Asserts on the GATE REASON rather than READY/BLOCK on purpose. A verdict depends on
// `autonomy.level`, so an assertion phrased as "must be READY" would break the day a human lowers
// the level — turning a real check into noise someone silences.

import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { delimiter, dirname, join } from 'node:path';

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
  [906, /closes abrahamFerga\/Plenipo#150\b/, 'a linked issue must be named in the run log, or a silent no-close is invisible'],
  [907, /closes nothing/, 'a pull request that will close nothing must say so before it merges'],
  [913, /closes other-org\/Other#151\b/, 'a cross-repository linked issue must retain its owner in the run log'],
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
  [911, true, 'UNKNOWN must block — only a known-clean state or repairable staleness is safe'],
  [912, false, 'UNSTABLE may pass only because required checks are evaluated independently'],
  [914, true, 'HAS_HOOKS must block — GitHub has an unsatisfied merge requirement'],
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
writeFileSync(join(scratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3, maxMergesPerTick: 20 } }));

// ── Platform policy — an agent verdict can approve a declared break, and conformance follows its
// workflow's path surface ───────────────────────────────────────────────────────────────────────
// Consumer conformance only runs for `src/**` and the root Directory props files. Requiring that
// check for a workflow-only change turns a skipped workflow into a permanent deadlock; skipping it
// for a source change lets a package break through. These three cases prove the two policies stay
// aligned, and that a platform break uses the same agent verdict as every other unattended merge.
const policyScratch = mkdtempSync(join(tmpdir(), 'merge-gate-platform-policy-'));
writeFileSync(
  join(policyScratch, 'workflow.json'),
  JSON.stringify({ stage: 'platform', autonomy: { level: 3, maxMergesPerTick: 20 } })
);
const policyFixture = join(policyScratch, 'policy-fixture.json');
writeFileSync(
  policyFixture,
  JSON.stringify([
    {
      number: 915,
      title: 'agent-approved workflow-only breaking policy change',
      body: 'plenipo-agent envelope\nSurface: breaking',
      isDraft: false,
      headRefName: 'fix/915-policy',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      trustedApproval: true,
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/agent-merge.yml' }],
    },
    {
      number: 916,
      title: 'breaking platform change without an agent verdict',
      body: 'plenipo-agent envelope\nSurface: breaking',
      isDraft: false,
      headRefName: 'fix/916-policy',
      baseRefName: 'main',
      labels: [],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/agent-merge.yml' }],
    },
    {
      number: 917,
      title: 'source change without a conformance result',
      body: 'plenipo-agent envelope\nSurface: additive',
      isDraft: false,
      headRefName: 'fix/917-policy',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'src/Plenipo.Core/Contract.cs' }],
    },
    {
      number: 921,
      title: 'a control change cannot use its own reviewer policy',
      body: 'plenipo-agent envelope\nSurface: none',
      isDraft: false,
      headRefName: 'fix/921-self-review',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      trustedPrGates: false,
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: '.github/workflows/pr-approval-verdict.md' }],
    },
    {
      number: 922,
      title: 'renaming a control file out keeps the old path protected',
      body: 'plenipo-agent envelope\nSurface: none',
      isDraft: false,
      headRefName: 'fix/922-rename-control',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      trustedPrGates: false,
      diff: '--- a/.github/workflows/agent-merge.yml\n+++ b/docs/agent-merge.yml\n',
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'docs/agent-merge.yml' }],
    },
    {
      number: 923,
      title: 'ordinary code cannot trust a free-floating approval label',
      body: 'plenipo-agent envelope\nSurface: none',
      isDraft: false,
      headRefName: 'fix/923-unproven-label',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      trustedApproval: false,
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      statusCheckRollup: [
        { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
        { name: 'Conformance verdict', workflowName: 'Consumer conformance', conclusion: 'SUCCESS' },
      ],
      files: [{ path: 'src/Plenipo.Core/Feature.cs' }],
    },
  ])
);

const policyRun = spawnSync(process.execPath, [gate, '--fixture', policyFixture], {
  encoding: 'utf8',
  cwd: policyScratch,
});

if (policyRun.status !== 0) {
  console.log(`  FAIL — platform policy fixture exited ${policyRun.status}\n${policyRun.stderr || policyRun.stdout}`);
  failed++;
} else {
  const policyReasons = (number) => {
    const lines = policyRun.stdout.split('\n');
    const start = lines.findIndex((line) => line.includes(`#${number} `));
    if (start === -1) return null;
    const rest = lines.slice(start + 1);
    const end = rest.findIndex((line) => /^\s{2}(READY|BLOCK|HELD|MERGED)/.test(line));
    return (end === -1 ? rest : rest.slice(0, end)).join('\n');
  };

  const ready915 = policyRun.stdout.split('\n').find((line) => line.includes('#915 '));
  const reasons915 = policyReasons(915);
  if (ready915 && /^\s{2}READY\b/.test(ready915) && !/surface_declared|consumers_green/.test(reasons915 ?? '')) {
    console.log('  ok   #915 — an agent-approved workflow-only platform policy change needs no human label or skipped conformance');
  } else {
    console.log(`  FAIL #915 — expected READY without surface/conformance failures; got:\n       ${ready915 ?? '(missing)'}\n${reasons915 ?? ''}`);
    failed++;
  }

  const reasons916 = policyReasons(916) ?? '';
  if (/surface_declared: .*agent:approved/i.test(reasons916)) {
    console.log('  ok   #916 — a breaking platform surface without the agent verdict remains blocked');
  } else {
    console.log(`  FAIL #916 — the breaking-surface rule did not name the required agent verdict:\n${reasons916}`);
    failed++;
  }

  const reasons917 = policyReasons(917) ?? '';
  if (/consumers_green/.test(reasons917)) {
    console.log('  ok   #917 — a platform source change still requires consumer conformance');
  } else {
    console.log(`  FAIL #917 — a platform source change lost its conformance requirement:\n${reasons917}`);
    failed++;
  }

  const reasons921 = policyReasons(921) ?? '';
  if (/trusted_pr_gates: .*protected-base PR gate rejected/i.test(reasons921)) {
    console.log('  ok   #921 — a PR-owned required-check wrapper cannot replace the protected-base evaluator');
  } else {
    console.log(`  FAIL #921 — a self-modifying control change was not rejected:\n${reasons921}`);
    failed++;
  }

  const reasons922 = policyReasons(922) ?? '';
  if (/trusted_pr_gates: .*protected-base PR gate rejected/i.test(reasons922)) {
    console.log('  ok   #922 — renaming a control file out of the control tree cannot hide its old path');
  } else {
    console.log(`  FAIL #922 — a control rename evaded the protected-base evaluator:\n${reasons922}`);
    failed++;
  }

  const reasons923 = policyReasons(923) ?? '';
  if (/trusted_agent_approval: .*approval-specific proof/i.test(reasons923)) {
    console.log('  ok   #923 — every unattended merge requires approval-specific provenance');
  } else {
    console.log(`  FAIL #923 — an ordinary PR trusted a free-floating approval label:\n${reasons923}`);
    failed++;
  }
}

// ── Required checks, not every informational workflow ──────────────────────
// A Copilot outage in the comment-only intent reviewer is not failed product CI. The approval
// label is the verdict gate; branch protection names the CI checks that must actually be green.
// This fixture models one required check plus an advisory `agent` job that failed externally.
const advisoryScratch = mkdtempSync(join(tmpdir(), 'merge-gate-advisory-check-'));
writeFileSync(
  join(advisoryScratch, 'workflow.json'),
  JSON.stringify({ stage: 'platform', autonomy: { level: 3, maxMergesPerTick: 20 } })
);
const advisoryFixture = join(advisoryScratch, 'required-checks-fixture.json');
writeFileSync(
  advisoryFixture,
  JSON.stringify({
    requiredCheckContexts: ['PR gates'],
    pullRequests: [
      {
        number: 918,
        title: 'a failed advisory agent job must not block required CI',
        body: 'plenipo-agent envelope\nSurface: none',
        isDraft: false,
        headRefName: 'fix/918-advisory',
        baseRefName: 'main',
        labels: [{ name: 'agent:approved' }],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'CLEAN',
        reviewDecision: '',
        statusCheckRollup: [
          { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
          { name: 'agent', workflowName: 'Review platform pull request intent', conclusion: 'FAILURE' },
        ],
        files: [{ path: 'tests/X.cs' }],
      },
      {
        number: 919,
        title: 'a missing required check still blocks despite an advisory success',
        body: 'plenipo-agent envelope\nSurface: none',
        isDraft: false,
        headRefName: 'fix/919-required',
        baseRefName: 'main',
        labels: [{ name: 'agent:approved' }],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'CLEAN',
        reviewDecision: '',
        statusCheckRollup: [{ name: 'agent', workflowName: 'Review platform pull request intent', conclusion: 'SUCCESS' }],
        files: [{ path: 'tests/X.cs' }],
      },
      {
        number: 924,
        title: 'failed optional Terraform check still blocks an infra change',
        body: 'plenipo-agent envelope\nSurface: none',
        isDraft: false,
        headRefName: 'fix/924-infra',
        baseRefName: 'main',
        labels: [{ name: 'agent:approved' }],
        mergeable: 'MERGEABLE',
        mergeStateStatus: 'UNSTABLE',
        reviewDecision: '',
        statusCheckRollup: [
          { name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' },
          { name: 'fmt / validate / plan', workflowName: 'Terraform PR Check', conclusion: 'FAILURE' },
        ],
        files: [{ path: 'infra/main.tf' }],
      },
    ],
  })
);

const advisoryRun = spawnSync(process.execPath, [gate, '--fixture', advisoryFixture], {
  encoding: 'utf8',
  cwd: advisoryScratch,
});
if (advisoryRun.status !== 0) {
  console.log(`  FAIL — required-check fixture exited ${advisoryRun.status}\n${advisoryRun.stderr || advisoryRun.stdout}`);
  failed++;
} else {
  const line918 = advisoryRun.stdout.split('\n').find((line) => line.includes('#918 '));
  if (line918 && /^\s{2}READY\b/.test(line918)) {
    console.log('  ok   #918 — an advisory model outage does not turn green required CI red');
  } else {
    console.log(`  FAIL #918 — expected READY with only required CI considered; got:\n       ${line918 ?? '(missing)'}`);
    failed++;
  }

  const reasons919 = advisoryRun.stdout
    .split('\n')
    .slice(advisoryRun.stdout.split('\n').findIndex((line) => line.includes('#919 ')) + 1)
    .join('\n');
  if (/checks_green: required check.*PR gates/i.test(reasons919)) {
    console.log('  ok   #919 — a missing required context remains a hard block');
  } else {
    console.log(`  FAIL #919 — missing required CI was not reported:\n${reasons919}`);
    failed++;
  }

  const reasons924 = advisoryRun.stdout
    .split('\n')
    .slice(advisoryRun.stdout.split('\n').findIndex((line) => line.includes('#924 ')) + 1)
    .join('\n');
  if (/infra_green: fmt \/ validate \/ plan \(FAILURE\) not passing/.test(reasons924)) {
    console.log('  ok   #924 — an infra-scoped Terraform failure remains blocking while unrelated optional checks stay advisory');
  } else {
    console.log(`  FAIL #924 — accepting UNSTABLE hid a Terraform failure:\n${reasons924}`);
    failed++;
  }
}

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
} else if (!/WOULD CLOSE other-org\/Other#151 with --repo other-org\/Other/.test(simulated.stdout)) {
  console.log('  FAIL — `--fixture --merge` did not preserve the linked issue repository in its simulated close command');
  failed++;
} else {
  console.log('  ok   simulate — `--fixture --merge` simulates, never reaches the network, and preserves issue ownership');
}

// A truncated file page must never be classified as low-risk. Otherwise a large PR can put docs
// in the visible page, code in the hidden remainder, and slip through autonomy level 1.
const truncatedScratch = mkdtempSync(join(tmpdir(), 'merge-gate-truncated-files-'));
writeFileSync(join(truncatedScratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 1 } }));
const truncatedFixture = join(truncatedScratch, 'fixture.json');
writeFileSync(
  truncatedFixture,
  JSON.stringify({
    requiredCheckContexts: ['PR gates'],
    pullRequests: [{
      number: 920,
      title: 'visible docs page hides additional files',
      body: 'plenipo-agent envelope',
      isDraft: false,
      headRefName: 'fix/920-truncated',
      baseRefName: 'main',
      labels: [{ name: 'agent:approved' }],
      mergeable: 'MERGEABLE',
      mergeStateStatus: 'CLEAN',
      reviewDecision: '',
      changedFiles: 101,
      statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }],
      files: [{ path: 'docs/README.md' }],
    }],
  })
);
const truncatedRun = spawnSync(process.execPath, [gate, '--fixture', truncatedFixture], {
  encoding: 'utf8',
  cwd: truncatedScratch,
});
if (truncatedRun.status === 0 && /BLOCK\s+#920\b/.test(truncatedRun.stdout) &&
    /level_permits: level 1 may merge docs, tests and the runbook only/.test(truncatedRun.stdout)) {
  console.log('  ok   truncation — an incomplete file page cannot masquerade as a low-risk PR');
} else {
  console.log(`  FAIL — an incomplete file page bypassed autonomy level 1:\n${truncatedRun.stdout}${truncatedRun.stderr}`);
  failed++;
}

// ── An unreadable required-check policy is infrastructure failure ───────────
// Ordinary blocked PRs keep the schedule green. Losing the API surface that defines required CI
// must not: that exact green-no-op ran for days while the merger was incapable of merging anything.
const failureScratch = mkdtempSync(join(tmpdir(), 'merge-gate-policy-read-'));
const fakeBin = join(failureScratch, 'bin');
mkdirSync(fakeBin);
writeFileSync(join(failureScratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3 } }));
const mockGh = join(fakeBin, 'gh-mock.mjs');
writeFileSync(
  mockGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 999, title: 'policy read failure', body: 'plenipo-agent envelope', isDraft: false, headRefName: 'fix/999-policy-read', baseRefName: 'main', labels: [{ name: 'agent:approved' }], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }], files: [{ path: 'tests/X.cs' }] }));\n` +
    `  process.exit(0);\n` +
    `}\n` +
    `console.error('required-check query unavailable');\n` +
    `process.exit(1);\n`
);

if (process.platform === 'win32') {
  writeFileSync(join(fakeBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(fakeBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}

const policyReadFailure = spawnSync(process.execPath, [gate, '--pr', '999'], {
  encoding: 'utf8',
  cwd: failureScratch,
  env: { ...process.env, GITHUB_ACTIONS: 'true', PATH: `${fakeBin}${delimiter}${process.env.PATH ?? ''}` },
});
const policyReadOutput = `${policyReadFailure.stdout}${policyReadFailure.stderr}`;
if (policyReadFailure.status !== 0 && /checks_configured: could not read required checks/.test(policyReadOutput) &&
    /::error title=Merge gate infrastructure failure::/.test(policyReadOutput)) {
  console.log('  ok   infrastructure — an unreadable required-check policy makes the scheduled merger red');
} else {
  console.log(`  FAIL — required-check discovery failed without failing the merger visibly:\n${policyReadOutput}`);
  failed++;
}

// `gh pr checks` uses exit 8 while checks are pending. Valid JSON on that status is queue state,
// not a broken policy API, and must not prevent unrelated ready PRs from being evaluated.
const pendingScratch = mkdtempSync(join(tmpdir(), 'merge-gate-pending-checks-'));
const pendingBin = join(pendingScratch, 'bin');
mkdirSync(pendingBin);
writeFileSync(join(pendingScratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3 } }));
const pendingGh = join(pendingBin, 'gh-mock.mjs');
writeFileSync(
  pendingGh,
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 998, title: 'pending required check', body: 'plenipo-agent envelope', isDraft: false, headRefName: 'fix/998-pending', headRefOid: '${'8'.repeat(40)}', baseRefName: 'main', labels: [], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', status: 'IN_PROGRESS' }], files: [{ path: 'tests/X.cs' }] }));\n` +
    `  process.exit(0);\n` +
    `}\n` +
    `if (args[0] === 'pr' && args[1] === 'checks') {\n` +
    `  console.log(JSON.stringify([{ name: 'PR gates' }]));\n` +
    `  process.exit(8);\n` +
    `}\n` +
    `if (args[0] === 'pr' && args[1] === 'diff') { console.log('diff --git a/tests/X.cs b/tests/X.cs'); process.exit(0); }\n` +
    `console.error('unexpected gh call: ' + args.join(' '));\n` +
    `process.exit(2);\n`
);
if (process.platform === 'win32') {
  writeFileSync(join(pendingBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(pendingBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}
const pendingCheck = spawnSync(process.execPath, [gate, '--pr', '998'], {
  encoding: 'utf8',
  cwd: pendingScratch,
  env: { ...process.env, GITHUB_ACTIONS: 'true', PATH: `${pendingBin}${delimiter}${process.env.PATH ?? ''}` },
});
const pendingOutput = `${pendingCheck.stdout}${pendingCheck.stderr}`;
if (pendingCheck.status === 0 && /checks_green: 1 check\(s\) still running/.test(pendingOutput) &&
    !/Merge gate infrastructure failure/.test(pendingOutput)) {
  console.log('  ok   pending — exit 8 with valid required-check JSON is ordinary queue state');
} else {
  console.log(`  FAIL — a pending required check stopped the merger infrastructure:\n${pendingOutput}`);
  failed++;
}

// GitHub reports the PR head branch on a pull_request_target run even though the workflow source is
// loaded from the protected base. The event, not headBranch, proves that source provenance.
const trustedScratch = mkdtempSync(join(tmpdir(), 'merge-gate-trusted-verdict-'));
const trustedBin = join(trustedScratch, 'bin');
const trustedHead = 'a'.repeat(40);
mkdirSync(trustedBin);
writeFileSync(join(trustedScratch, 'workflow.json'), JSON.stringify({ autonomy: { level: 3 } }));
const trustedGh = join(trustedBin, 'gh-mock.mjs');
writeFileSync(
  trustedGh,
  `import { mkdirSync, writeFileSync } from 'node:fs';\n` +
  `const args = process.argv.slice(2);\n` +
    `if (args[0] === 'pr' && args[1] === 'view') {\n` +
    `  console.log(JSON.stringify({ number: 997, title: 'trusted control change', body: 'plenipo-agent envelope', isDraft: false, headRefName: 'fix/997-control', headRefOid: '${trustedHead}', baseRefName: 'main', labels: [{ name: 'agent:approved' }], mergeable: 'MERGEABLE', mergeStateStatus: 'CLEAN', reviewDecision: '', statusCheckRollup: [{ name: 'PR gates', workflowName: 'Agent gates', conclusion: 'SUCCESS' }], files: [{ path: '.github/workflows/agent-merge.yml' }] }));\n` +
    `  process.exit(0);\n` +
  `}\n` +
  `if (args[0] === 'pr' && args[1] === 'checks') { console.log('[{"name":"PR gates"}]'); process.exit(0); }\n` +
  `if (args[0] === 'pr' && args[1] === 'diff') { console.log('--- a/.github/workflows/agent-merge.yml\\n+++ b/.github/workflows/agent-merge.yml'); process.exit(0); }\n` +
  `if (args[0] === 'repo' && args[1] === 'view') { console.log('{"nameWithOwner":"example/repo"}'); process.exit(0); }\n` +
  `if (args[0] === 'api' && args.includes('--slurp')) { console.log(JSON.stringify([[{ body: '<!-- plenipo-agent-verdict:v1 run=300 -->', html_url: 'https://github.com/example/repo/pull/997#issuecomment-1', created_at: '2026-08-10T10:10:00Z', updated_at: '2026-08-10T10:10:00Z' }]])); process.exit(0); }\n` +
  `if (args[0] === 'api' && args[1] === 'graphql') { console.log('{"data":{"repository":{"pullRequest":{"lastEditedAt":null}}}}'); process.exit(0); }\n` +
  `if (args[0] === 'api' && args.some((arg) => arg.includes('contents/.github/scripts/pr-gates.mjs'))) { console.log('process.exit(0);'); process.exit(0); }\n` +
  `if (args[0] === 'run' && args[1] === 'view') { console.log(JSON.stringify({ databaseId: 300, displayTitle: 'Approval verdict PR #997 @ ${trustedHead} -> main', event: 'pull_request_target', headBranch: 'fix/997-control', status: 'completed', conclusion: 'success', createdAt: '2026-08-10T10:00:00Z' })); process.exit(0); }\n` +
  `if (args[0] === 'run' && args[1] === 'list') {\n` +
    `  console.log(JSON.stringify([{ databaseId: 300, displayTitle: 'Approval verdict PR #997 @ ${trustedHead} -> main', event: 'pull_request_target', headBranch: 'fix/997-control', status: 'completed', conclusion: 'success', createdAt: '2026-08-10T10:00:00Z' }]));\n` +
    `  process.exit(0);\n` +
    `}\n` +
  `if (args[0] === 'run' && args[1] === 'download') { const dir = args[args.indexOf('--dir') + 1]; mkdirSync(dir, { recursive: true }); writeFileSync(dir + '/safe-output-items.jsonl', JSON.stringify({ type: 'add_comment', url: 'https://github.com/example/repo/pull/997#issuecomment-1' }) + '\\n' + JSON.stringify({ type: 'add_labels', number: 997, labelsAdded: ['agent:approved'] }) + '\\n'); process.exit(0); }\n` +
    `console.error('unexpected gh call: ' + args.join(' '));\n` +
    `process.exit(2);\n`
);
if (process.platform === 'win32') {
  writeFileSync(join(trustedBin, 'gh.cmd'), `@echo off\r\n"${process.execPath}" "%~dp0\\gh-mock.mjs" %*\r\n`);
} else {
  const shim = join(trustedBin, 'gh');
  writeFileSync(shim, `#!/bin/sh\nexec "${process.execPath}" "$(dirname "$0")/gh-mock.mjs" "$@"\n`);
  chmodSync(shim, 0o755);
}
const trustedVerdict = spawnSync(process.execPath, [gate, '--pr', '997'], {
  encoding: 'utf8',
  cwd: trustedScratch,
  env: { ...process.env, PATH: `${trustedBin}${delimiter}${process.env.PATH ?? ''}` },
});
const trustedOutput = `${trustedVerdict.stdout}${trustedVerdict.stderr}`;
if (trustedVerdict.status === 0 && /READY\s+#997\b/.test(trustedOutput) &&
    !/trusted_agent_approval:/.test(trustedOutput) && !/trusted_pr_gates:/.test(trustedOutput)) {
  console.log('  ok   base verdict — exact approval output and protected-base gates are independently proven');
} else {
  console.log(`  FAIL — a protected-base pull_request_target verdict was rejected:\n${trustedOutput}`);
  failed++;
}

if (failed) {
  console.log(`\n${failed} rollup case(s) wrong. merge-gate is the last automated thing before main — do not merge this.\n`);
  process.exit(1);
}
console.log(`\nOK — ${cases.length} rollup, ${closeCases.length} linked-issue, ${mergeableCases.length} mergeable, 4 platform-policy, 2 required-context, 3 stale-routing, 1 truncation, 1 simulation, 1 pending-state, 1 base-verdict and 1 infrastructure-failure case(s) behave correctly.\n`);
