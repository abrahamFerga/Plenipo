#!/usr/bin/env node
// Static policy test for the trusted approval-expiry workflow.
//
//   node .github/scripts/agent-approval-reset.test.mjs
//
// This workflow is deliberately tiny and does not execute PR code, so the meaningful regression
// test is that its event wiring continues to clear a verdict precisely when a PR's effective diff
// or evidence changes. A title edit must not discard a review; a body edit must, because runtime
// evidence is part of the verdict.

import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const assetWorkflow = join(here, 'agent-approval-reset.yml');
const installedWorkflow = join(here, '..', 'workflows', 'agent-approval-reset.yml');
const source = readFileSync(existsSync(assetWorkflow) ? assetWorkflow : installedWorkflow, 'utf8');

const expectations = [
  [/pull_request_target:\s*\r?\n\s+types:\s*\[synchronize, edited, reopened\]/, 'uses trusted default-branch event wiring for every diff-changing PR event'],
  [/github\.event_name != 'pull_request_target'/, 'does not accidentally treat every edited PR-target event as a reset'],
  [/github\.event\.action != 'edited'/, 'resets synchronizations and reopen events'],
  [/github\.event\.changes\.body != null/, 'expires a verdict when its evidence-bearing body changes'],
  [/github\.event\.changes\.base != null/, 'expires a verdict when retargeting changes the effective diff'],
  [/for label in 'agent:approved' 'agent:changes-requested'; do/, 'clears both mutually exclusive verdict labels'],
  [/\*'\(HTTP 404\)'\*\) echo "not set/, 'treats only a missing label as a successful delete'],
  [/exit 1/, 'fails visibly when a stale verdict cannot be cleared'],
];

let failed = 0;
for (const [pattern, why] of expectations) {
  if (pattern.test(source)) console.log(`  ok   ${why}`);
  else {
    console.log(`  FAIL — ${why}`);
    failed++;
  }
}

if (failed) {
  console.log(`\n${failed} approval-expiry policy case(s) wrong. A new diff must never retain an old agent verdict.\n`);
  process.exit(1);
}

console.log(`\nOK — ${expectations.length} approval-expiry policy case(s) behave correctly.\n`);
