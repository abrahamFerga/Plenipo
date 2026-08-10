#!/usr/bin/env node
// Pure regression tests for the approval provenance proof. No GitHub calls or artifact downloads.

import { evaluateApprovalEvidence, verdictTitle } from './approval-proof.mjs';

const pr = {
  number: 42,
  headRefOid: 'a'.repeat(40),
  baseRefName: 'main',
};
const comment = {
  body: '<!-- plenipo-agent-verdict:v1 run=100 -->',
  html_url: 'https://github.com/example/repo/pull/42#issuecomment-1',
  created_at: '2026-08-10T10:10:00Z',
  updated_at: '2026-08-10T10:10:00Z',
};
const run = {
  databaseId: 100,
  displayTitle: verdictTitle(pr),
  event: 'pull_request_target',
  headBranch: 'fix/42-proof',
  status: 'completed',
  conclusion: 'success',
  createdAt: '2026-08-10T10:00:00Z',
};
const approvalManifest = [
  { type: 'add_comment', url: comment.html_url },
  { type: 'add_labels', number: 42, labelsAdded: ['agent:approved'] },
];

const evidence = (overrides = {}) => ({
  comments: [comment],
  runsById: new Map([[100, run]]),
  manifestsByRunId: new Map([[100, approvalManifest]]),
  lastEditedAt: '2026-08-10T09:59:00Z',
  recentRuns: [run],
  ...overrides,
});

const cases = [
  {
    name: 'approval output from a protected-base exact-head run is trusted',
    result: evaluateApprovalEvidence(pr, evidence()),
    ok: true,
  },
  {
    name: 'workflow success without an approval output is not approval',
    result: evaluateApprovalEvidence(pr, evidence({
      manifestsByRunId: new Map([[100, [{ type: 'add_comment', url: comment.html_url }]]]),
    })),
    ok: false,
  },
  {
    name: 'an attested changes-requested result supersedes a free-floating approval label',
    result: evaluateApprovalEvidence(pr, evidence({
      manifestsByRunId: new Map([[100, [
        { type: 'add_comment', url: comment.html_url },
        { type: 'add_labels', number: 42, labelsAdded: ['agent:changes-requested'] },
      ]]]),
    })),
    ok: false,
    why: /requested changes/,
  },
  {
    name: 'editing runtime evidence after review expires the verdict without a new commit',
    result: evaluateApprovalEvidence(pr, evidence({ lastEditedAt: '2026-08-10T10:01:00Z' })),
    ok: false,
  },
  {
    name: 'a newer unresolved reviewer run supersedes an older approval',
    result: evaluateApprovalEvidence(pr, evidence({
      recentRuns: [run, { ...run, databaseId: 101, createdAt: '2026-08-10T10:02:00Z' }],
    })),
    ok: false,
    why: /newer verdict run 101/,
  },
  {
    name: 'an edited marker comment cannot point at a different historical run',
    result: evaluateApprovalEvidence(pr, evidence({
      comments: [{ ...comment, updated_at: '2026-08-10T10:11:00Z' }],
    })),
    ok: false,
  },
  {
    name: 'a dispatch must execute from the protected base branch',
    result: evaluateApprovalEvidence(pr, evidence({
      runsById: new Map([[100, { ...run, event: 'workflow_dispatch', headBranch: 'fix/42-proof' }]]),
    })),
    ok: false,
  },
  {
    name: 'a dispatch from the protected base may attest approval',
    result: evaluateApprovalEvidence(pr, evidence({
      runsById: new Map([[100, { ...run, event: 'workflow_dispatch', headBranch: 'main' }]]),
      recentRuns: [{ ...run, event: 'workflow_dispatch', headBranch: 'main' }],
    })),
    ok: true,
  },
];

let failed = 0;
for (const test of cases) {
  const matches = test.result.ok === test.ok && (!test.why || test.why.test(test.result.why ?? ''));
  if (matches) console.log(`  ok   ${test.name}`);
  else {
    console.log(`  FAIL ${test.name}: ${JSON.stringify(test.result)}`);
    failed++;
  }
}

if (failed) {
  console.log(`\n${failed} approval-proof case(s) failed. A label is never sufficient merge authority.\n`);
  process.exit(1);
}
console.log(`\nOK — ${cases.length} approval-proof cases bind verdict outcome, source and revision.\n`);
