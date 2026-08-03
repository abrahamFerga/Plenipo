#!/usr/bin/env node
// Deterministic pull-request gates (verification ladder L1/L2). No dependencies — node >= 18.
//
//   node .github/scripts/pr-gates.mjs <path-to-unified-diff>
//
//   env: PR_BODY      the pull request body
//        PR_HEAD_REF  the head branch name
//        PR_LABELS    comma-separated label names
//
// Exits 0 when every gate passes, 1 with every failure listed. Runs as a required status check, so
// it is what actually stands between an agent and `main` — the prose in a skill file does not.
//
// Two groups of gates:
//
//   Evidence gates run only on loop branches (feat/*, fix/*, chore/*). They assert the PR body
//   carries what a reviewer needs: the issue it closes, what was actually exercised at runtime, and
//   a regression test seen red before the fix. A human's PR is not held to the agent's body format.
//
//   The spine guard runs on EVERY pull request, including a human's. It fires when a diff REMOVES
//   or MODIFIES a line touching tenant isolation, the approval gate, permission grants, role
//   baselines, CI itself, or the package feed. Adding such a line is ordinary feature work;
//   editing or deleting one is a change to the thing the platform exists to guarantee. Override
//   deliberately with the `human-approved` label — which is a human act, recorded on the PR.

import { readFileSync, existsSync } from 'node:fs';

const diffPath = process.argv[2];
const body = process.env.PR_BODY ?? '';
const headRef = process.env.PR_HEAD_REF ?? '';
const labels = (process.env.PR_LABELS ?? '')
  .split(',')
  .map((s) => s.trim().toLowerCase())
  .filter(Boolean);

const isLoopPr = /^(feat|fix|chore)\//.test(headRef);
const humanApproved = labels.includes('human-approved');

const failures = [];
const passes = [];
const gate = (name, ok, why) => (ok ? passes.push(name) : failures.push(`${name}: ${why}`));

// ── Evidence gates — loop branches only ──────────────────────────────────────
if (isLoopPr) {
  gate(
    'closes_an_issue',
    /\bcloses #\d+/i.test(body),
    'the body has no "Closes #<n>". Without it the issue never closes and the board rots.'
  );

  // A heading alone is not evidence; require content under it. Split on lines rather than with a
  // lookahead: JS has no \Z, and `$` under /m/ would end the section at the first newline.
  const section = (heading) => {
    const lines = body.split('\n');
    const head = new RegExp(`^#{1,4}\\s*${heading}\\s*$`, 'i');
    const start = lines.findIndex((l) => head.test(l.trim()));
    if (start === -1) return '';
    const rest = lines.slice(start + 1);
    const end = rest.findIndex((l) => /^#{1,4}\s/.test(l));
    return (end === -1 ? rest : rest.slice(0, end)).join('\n').trim();
  };

  const evidence = section('Runtime evidence');
  gate(
    'has_runtime_evidence',
    evidence.length > 40,
    'no "## Runtime evidence" section with the request exercised and the output observed. ' +
      'A green build proves the code is well formed and nothing else.'
  );

  const regression = section('Regression test');
  gate(
    'has_red_before_green',
    /red/i.test(regression) && /green/i.test(regression),
    'no "## Regression test" section stating the test was seen red before the fix and green after. ' +
      'A test never seen red is not a regression test.'
  );
}

// ── Spine guard — every pull request ─────────────────────────────────────────
const CONTENT_RULES = [
  [/HasQueryFilter/, 'tenant isolation (HasQueryFilter)'],
  [/RequiresApproval/, 'the approval gate (RequiresApproval)'],
  [/AddPlenipoRole/, 'a role baseline (AddPlenipoRole)'],
  [/Permissions\s*\./, 'a permission string'],
];

const PATH_RULES = [
  [/^\.github\//, 'CI and workflow configuration'],
  [/(^|\/)CODEOWNERS$/, 'CODEOWNERS'],
  [/(^|\/)nuget\.config$/i, 'the package feed'],
  [/appsettings[^/]*\.json$/i, 'runtime configuration'],
];

if (!diffPath || !existsSync(diffPath)) {
  console.error(`pr-gates: cannot read the diff at "${diffPath}"`);
  process.exit(1);
}

const hits = [];
let file = '';
for (const line of readFileSync(diffPath, 'utf8').split('\n')) {
  if (line.startsWith('+++ ')) {
    file = line.slice(4).replace(/^b\//, '').trim();
    for (const [re, what] of PATH_RULES) {
      if (re.test(file)) hits.push(`${file} — ${what}`);
    }
    continue;
  }
  // A modified line shows up as both '-' and '+', so scanning removals catches edits AND deletions
  // while leaving pure additions alone.
  if (line.startsWith('-') && !line.startsWith('---')) {
    for (const [re, what] of CONTENT_RULES) {
      if (re.test(line)) hits.push(`${file} — removes or edits ${what}: ${line.slice(1).trim()}`);
    }
  }
}

const unique = [...new Set(hits)];
if (unique.length && !humanApproved) {
  failures.push(
    'spine_untouched: this diff changes something the platform exists to guarantee.\n' +
      unique.map((h) => `      - ${h}`).join('\n') +
      '\n      A human must review it. Add the `human-approved` label to override, deliberately.'
  );
} else if (unique.length) {
  passes.push('spine_untouched (overridden by human-approved)');
}

// ── Report ───────────────────────────────────────────────────────────────────
for (const p of passes) console.log(`  ok   ${p}`);
if (failures.length) {
  console.log('');
  for (const f of failures) console.log(`  FAIL ${f}`);
  console.log(`\n${failures.length} gate(s) failed.\n`);
  process.exit(1);
}
console.log(`\nOK — ${passes.length} gate(s) passed${isLoopPr ? '' : ' (not a loop branch: evidence gates skipped)'}.\n`);
