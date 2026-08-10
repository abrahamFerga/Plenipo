#!/usr/bin/env node
// Self-test for the deterministic PR spine gate. No network and no repository state.
//
//   node .github/scripts/pr-gates.test.mjs
//
// The review verdict is deliberately separate from this L1/L2 gate, but an unattended merge must
// be able to use that verdict for a protected diff. These cases keep the policy fail-closed when a
// verdict is absent, withdrawn, or contradicted.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const gate = join(here, 'pr-gates.mjs');
const scratch = mkdtempSync(join(tmpdir(), 'pr-gates-'));
const diff = join(scratch, 'protected.diff');
const renameDiff = join(scratch, 'rename-out.diff');
const deleteDiff = join(scratch, 'delete.diff');

writeFileSync(
  diff,
  [
    'diff --git a/.github/workflows/agent-merge.yml b/.github/workflows/agent-merge.yml',
    '--- a/.github/workflows/agent-merge.yml',
    '+++ b/.github/workflows/agent-merge.yml',
    '@@ -1 +1 @@',
    '-name: Agent merge',
    '+name: Agent merge with a deterministic policy',
    '',
  ].join('\n')
);

writeFileSync(renameDiff, [
  'diff --git a/.github/workflows/agent-merge.yml b/docs/agent-merge.yml',
  'similarity index 100%',
  'rename from .github/workflows/agent-merge.yml',
  'rename to docs/agent-merge.yml',
  '--- a/.github/workflows/agent-merge.yml',
  '+++ b/docs/agent-merge.yml',
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

const body = [
  'Closes #1',
  '',
  '## Runtime evidence',
  'The protected workflow was exercised against a fixture and its exact gate result was observed.',
  '',
  '## Regression test',
  'The case was seen red before the policy repair and green after the agent verdict was accepted.',
].join('\n');

const run = (labels, diffPath = diff) => {
  const result = spawnSync(process.execPath, [gate, diffPath], {
    encoding: 'utf8',
    env: {
      ...process.env,
      PR_BODY: body,
      PR_HEAD_REF: 'fix/agent-verdict-policy',
      PR_LABELS: labels,
    },
  });
  return { status: result.status, output: `${result.stdout}${result.stderr}` };
};

const cases = [
  {
    labels: '',
    status: 1,
    pattern: /spine_untouched/,
    why: 'a protected diff without a verdict must fail closed',
  },
  {
    labels: 'agent:approved',
    status: 0,
    pattern: /spine_untouched \(overridden by agent:approved\)/,
    why: 'the approval workflow verdict must be sufficient for an unattended protected change',
  },
  {
    labels: 'agent:approved,agent:changes-requested',
    status: 1,
    pattern: /spine_untouched: .*agent verdict/i,
    why: 'a withdrawn verdict must never remain an override',
  },
  {
    labels: 'agent:approved,needs-human',
    status: 1,
    pattern: /spine_untouched: .*agent verdict/i,
    why: 'a human-escalation label must withdraw the automatic override',
  },
];

let failed = 0;
for (const test of cases) {
  const result = run(test.labels);
  if (result.status === test.status && test.pattern.test(result.output)) {
    console.log(`  ok   ${test.labels || '(no labels)'} — ${test.why}`);
  } else {
    console.log(
      `  FAIL ${test.labels || '(no labels)'} — expected exit ${test.status} and ${test.pattern}; got exit ${result.status}.\n` +
        `       ${test.why}\n       output:\n${result.output}`
    );
    failed++;
  }
}

for (const [name, diffPath] of [['rename out of the control tree', renameDiff], ['control deletion', deleteDiff]]) {
  const result = run('', diffPath);
  if (result.status === 1 && /spine_untouched/.test(result.output) && /agent-merge\.yml/.test(result.output)) {
    console.log(`  ok   ${name} keeps the protected old path`);
  } else {
    console.log(`  FAIL ${name} evaded the old-path guard:\n${result.output}`);
    failed++;
  }
}

if (failed) {
  console.log(`\n${failed} PR-gate policy case(s) wrong. A label must not turn a contradictory verdict into permission.\n`);
  process.exit(1);
}

console.log(`\nOK — ${cases.length + 2} protected-diff agent-verdict case(s) behave correctly.\n`);
