#!/usr/bin/env node
// Self-test for the deterministic PR evidence and protected-diff gates. No network or repo state.
//
//   node .github/scripts/pr-gates.test.mjs
//
// Protected diffs require substantive evidence regardless of branch ownership. Unprotected attended
// work stays lightweight, while loop branches additionally have to close their source issue.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const gate = join(here, 'pr-gates.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'pr-gates-'));
const protectedDiff = join(scratch, 'protected-manifest.diff');
const githubConfigDiff = join(scratch, 'github-config.diff');
const unprotectedDiff = join(scratch, 'unprotected.diff');
const invariantRemovalDiff = join(scratch, 'invariant-removal.diff');
const invariantProseDiff = join(scratch, 'invariant-prose.diff');
const renameDiff = join(scratch, 'rename-out.diff');
const deleteDiff = join(scratch, 'delete.diff');

writeFileSync(protectedDiff, [
  'diff --git a/plugins/plenipo/.claude-plugin/plugin.json b/plugins/plenipo/.claude-plugin/plugin.json',
  '--- a/plugins/plenipo/.claude-plugin/plugin.json',
  '+++ b/plugins/plenipo/.claude-plugin/plugin.json',
  '@@ -1 +1 @@',
  '-{"version":"1.0.0"}',
  '+{"version":"1.0.1"}',
  '',
].join('\n'));
writeFileSync(githubConfigDiff, [
  'diff --git a/.github/dependabot.yml b/.github/dependabot.yml',
  '--- a/.github/dependabot.yml',
  '+++ b/.github/dependabot.yml',
  '@@ -1 +1 @@',
  '-version: 1',
  '+version: 2',
  '',
].join('\n'));
writeFileSync(invariantRemovalDiff, [
  'diff --git a/src/TenantDbContext.cs b/src/TenantDbContext.cs',
  '--- a/src/TenantDbContext.cs',
  '+++ b/src/TenantDbContext.cs',
  '@@ -1 +1 @@',
  '-builder.Entity<Order>().HasQueryFilter(order => order.TenantId == tenant.Id);',
  '+builder.Entity<Order>();',
  '',
].join('\n'));
writeFileSync(invariantProseDiff, [
  'diff --git a/README.md b/README.md',
  '--- a/README.md',
  '+++ b/README.md',
  '@@ -1 +1 @@',
  '-The implementation uses HasQueryFilter for tenant isolation.',
  '+Tenant isolation is enforced by the implementation.',
  '',
].join('\n'));
writeFileSync(unprotectedDiff, [
  'diff --git a/README.md b/README.md',
  '--- a/README.md',
  '+++ b/README.md',
  '@@ -1 +1 @@',
  '-Old prose',
  '+New prose',
  '',
].join('\n'));
writeFileSync(renameDiff, [
  'diff --git a/.github/workflows/agent-merge.yml b/docs/agent-merge.yml',
  'similarity index 100%',
  'rename from .github/workflows/agent-merge.yml',
  'rename to docs/agent-merge.yml',
  '',
].join('\n'));
writeFileSync(deleteDiff, [
  'diff --git a/.github/workflows/agent-merge.yml b/.github/workflows/agent-merge.yml',
  'deleted file mode 100644',
  '--- a/.github/workflows/agent-merge.yml',
  '+++ /dev/null',
  '@@ -1 +0,0 @@',
  '-name: Agent merge',
  '',
].join('\n'));

const evidenceBody = [
  '## Runtime evidence',
  'The protected workflow was exercised against a fixture and its exact gate result was observed.',
  '',
  '## Regression test',
  'The fixture was seen red before the policy repair and green after the implementation changed.',
].join('\n');
const validEnvelope = '<!-- plenipo-agent kind=handoff from=plenipo-agents ref=plenipo-agents#39 status=open -->';
const malformedEnvelope = '<!-- plenipo-agent kind=handoff from=plenipo-agents status=maybe -->';
const loopBody = `${validEnvelope}\n\nCloses #1\n\n${evidenceBody}`;

const lockedControlPaths = [
  '.github/scripts/pr-gates.mjs',
  '.github/scripts/merge-gate.mjs',
  '.github/workflows/agent-gates.yml',
  '.github/workflows/agent-merge.yml',
  '.github/workflows/ci.yml',
  'workflow.json',
  'plugins/plenipo/skills/setup/assets/pr-gates.mjs',
  'plugins/plenipo/skills/setup/assets/merge-gate.mjs',
  'plugins/plenipo/skills/setup/assets/agent-gates.yml',
  'plugins/plenipo/skills/setup/assets/agent-merge.yml',
];
const controlDiffs = lockedControlPaths.map((path, index) => {
  const diffPath = join(scratch, `control-${index}.diff`);
  writeFileSync(diffPath, [
    `diff --git a/${path} b/${path}`,
    `--- a/${path}`,
    `+++ b/${path}`,
    '@@ -1 +1 @@',
    '-enforce: true',
    '+enforce: false',
    '',
  ].join('\n'));
  return [path, diffPath];
});

const run = (diffPath, { headRef = 'docs/attended-task', prBody = evidenceBody } = {}) => {
  const result = spawnSync(process.execPath, [gate, diffPath], {
    encoding: 'utf8',
    env: {
      ...process.env,
      PR_BODY: prBody,
      PR_HEAD_REF: headRef,
    },
  });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
};

const cases = [
  {
    name: 'attended protected manifest with evidence',
    result: () => run(protectedDiff),
    status: 0,
    patterns: [/protected_diff_detected/, /has_runtime_evidence/, /has_red_before_green/],
    why: 'deterministic evidence authorizes an ordinary protected diff',
  },
  {
    name: 'attended protected manifest without evidence',
    result: () => run(protectedDiff, { prBody: '' }),
    status: 1,
    patterns: [/has_runtime_evidence/, /has_red_before_green/, /protected_diff_detected/],
    why: 'protected changes must fail closed when their evidence is absent',
  },
  {
    name: 'attended protected manifest with weak evidence',
    result: () => run(protectedDiff, {
      prBody: '## Runtime evidence\nRan it.\n\n## Regression test\nThe check stayed green.',
    }),
    status: 1,
    patterns: [/has_runtime_evidence/, /has_red_before_green/],
    why: 'headings and a green-only claim are not substantive regression evidence',
  },
  {
    name: 'ordinary GitHub config with evidence',
    result: () => run(githubConfigDiff),
    status: 0,
    patterns: [/protected_diff_detected/, /has_runtime_evidence/, /has_red_before_green/],
    absent: [/control_policy_locked/],
    why: 'the immutable list does not accidentally lock every .github path',
  },
  {
    name: 'security invariant removal with evidence',
    result: () => run(invariantRemovalDiff),
    status: 1,
    patterns: [/control_policy_locked/, /HasQueryFilter/],
    why: 'body evidence cannot authorize removal of tenant isolation',
  },
  {
    name: 'security invariant prose edit',
    result: () => run(invariantProseDiff),
    status: 0,
    patterns: [/not a loop branch: evidence gates skipped/],
    absent: [/control_policy_locked/],
    why: 'removing a source symbol from Markdown is not removing the executable invariant',
  },
  {
    name: 'traditional candidate missing protocol envelope',
    result: () => run(unprotectedDiff, {
      headRef: 'fix/evidence-policy',
      prBody: `Closes #1\n\n${evidenceBody}`,
    }),
    status: 1,
    patterns: [/protocol_envelope/],
    why: 'traditional unattended work cannot pass CI then stall at the merger',
  },
  {
    name: 'traditional candidate with malformed protocol envelope',
    result: () => run(unprotectedDiff, {
      headRef: 'feat/evidence-policy',
      prBody: `${malformedEnvelope}\n\nCloses #1\n\n${evidenceBody}`,
    }),
    status: 1,
    patterns: [/protocol_envelope/],
    why: 'a near-match cannot impersonate the opening protocol envelope',
  },
  {
    name: 'loop change without source issue',
    result: () => run(unprotectedDiff, {
      headRef: 'fix/evidence-policy',
      prBody: `${validEnvelope}\n\n${evidenceBody}`,
    }),
    status: 1,
    patterns: [/protocol_envelope/, /closes_an_issue/],
    why: 'Closes remains required for loop-owned work',
  },
  {
    name: 'loop change with complete evidence',
    result: () => run(unprotectedDiff, { headRef: 'fix/evidence-policy', prBody: loopBody }),
    status: 0,
    patterns: [/protocol_envelope/, /closes_an_issue/, /has_runtime_evidence/, /has_red_before_green/],
    why: 'an ordinary loop PR passes with its issue and evidence',
  },
  {
    name: 'attended unprotected noncandidate change',
    result: () => run(unprotectedDiff, {
      headRef: 'docs/attended-task',
      prBody: 'Attended prose mentioning plenipo-agent without a protocol marker.',
    }),
    status: 0,
    patterns: [/not a loop branch: evidence gates skipped/],
    why: 'unprotected attended work outside candidate prefixes stays exempt',
  },
  {
    name: 'protected attended change does not need Closes',
    result: () => run(protectedDiff),
    status: 0,
    patterns: [/has_runtime_evidence/, /has_red_before_green/],
    absent: [/closes_an_issue/],
    why: 'protected evidence applies globally, but issue closure applies only to loop PRs',
  },
  {
    name: 'codex candidate missing protocol envelope',
    result: () => run(unprotectedDiff, {
      headRef: 'codex/token-efficient-agent-models',
      prBody: `Closes #1\n\n${evidenceBody}`,
    }),
    status: 1,
    patterns: [/protocol_envelope/],
    why: 'codex unattended work cannot silently fall out of required checks',
  },
  {
    name: 'codex candidate with malformed protocol envelope',
    result: () => run(unprotectedDiff, {
      headRef: 'codex/verdict-marker-only',
      prBody: `<!-- plenipo-agent-verdict:v1 run=300 -->\n\nCloses #1\n\n${evidenceBody}`,
    }),
    status: 1,
    patterns: [/protocol_envelope/],
    why: 'a verdict proof marker cannot impersonate the loop envelope',
  },
  {
    name: 'codex candidate with valid protocol envelope',
    result: () => run(unprotectedDiff, {
      headRef: 'codex/token-efficient-agent-models',
      prBody: loopBody,
    }),
    status: 0,
    patterns: [/protocol_envelope/, /closes_an_issue/, /has_runtime_evidence/, /has_red_before_green/],
    why: 'a valid opening envelope aligns the required gate and scheduled merger',
  },
];

let failed = 0;
for (const test of cases) {
  const result = test.result();
  const patternsMatch = test.patterns.every((pattern) => pattern.test(result.output));
  const absentMatch = (test.absent ?? []).every((pattern) => !pattern.test(result.output));
  if (result.status === test.status && patternsMatch && absentMatch) {
    console.log(`  ok   ${test.name} — ${test.why}`);
  } else {
    console.log(
      `  FAIL ${test.name} — expected exit ${test.status}, patterns ${test.patterns}, and absent ${test.absent ?? []}; ` +
        `got exit ${result.status}.\n       ${test.why}\n       output:\n${result.output}`
    );
    failed++;
  }
}

for (const [path, diffPath] of controlDiffs) {
  const result = run(diffPath);
  if (result.status === 1 && /control_policy_locked/.test(result.output) && result.output.includes(path)) {
    console.log(`  ok   locked control edit — ${path}`);
  } else {
    console.log(`  FAIL control edit escaped the immutable policy — ${path}:\n${result.output}`);
    failed++;
  }
}

for (const [name, diffPath] of [['rename out of the control tree', renameDiff], ['control deletion', deleteDiff]]) {
  const result = run(diffPath, { prBody: '' });
  if (
    result.status === 1 &&
    /control_policy_locked/.test(result.output) &&
    /agent-merge\.yml/.test(result.output) &&
    !/agent:approved|human-approved/.test(result.output)
  ) {
    console.log(`  ok   ${name} keeps the protected old path`);
  } else {
    console.log(`  FAIL ${name} evaded the old-path guard:\n${result.output}`);
    failed++;
  }
}

if (failed) {
  console.log(`\n${failed} PR-gate evidence policy case(s) wrong. Protected permission must come from proof.\n`);
  process.exit(1);
}

console.log(`\nOK — ${cases.length + controlDiffs.length + 2} deterministic PR-gate case(s) behave correctly.\n`);
