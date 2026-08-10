// Verify that the current `agent:approved` label came from the protected-base reviewer for the
// exact PR head, base and body revision. A label alone has no provenance: any workflow with
// pull-request write permission can apply the same text.

import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export const VERDICT_WORKFLOW = 'pr-approval-verdict.lock.yml';
const markerPattern = () => /<!--\s*plenipo-agent-verdict:v1\s+run=(\d+)\s*-->/g;

export const verdictTitle = (pr) =>
  `Approval verdict PR #${pr.number} @ ${pr.headRefOid} -> ${pr.baseRefName ?? 'main'}`;

const time = (value) => {
  const parsed = Date.parse(value ?? '');
  return Number.isNaN(parsed) ? -Infinity : parsed;
};

const sameUrl = (left, right) => String(left ?? '').replace(/\/$/, '') === String(right ?? '').replace(/\/$/, '');

function markerRunIds(comment) {
  // An edited comment is not immutable evidence. Someone could otherwise replace a negative run
  // id with an older approval run while retaining the trusted workflow's comment URL.
  if (comment.created_at && comment.updated_at && comment.created_at !== comment.updated_at) return [];
  return [...String(comment.body ?? '').matchAll(markerPattern())].map((match) => Number(match[1]));
}

function sourceIsTrusted(run, pr) {
  return run.event === 'pull_request_target' ||
    (run.event === 'workflow_dispatch' && run.headBranch === pr.baseRefName);
}

function manifestOutcome(items, comment, pr) {
  const commentWasCreated = items.some((item) =>
    item.type === 'add_comment' && sameUrl(item.url, comment.html_url));
  if (!commentWasCreated) return null;

  const labels = items
    .filter((item) => item.type === 'add_labels' && Number(item.number) === Number(pr.number))
    .flatMap((item) => item.labelsAdded ?? []);
  const approved = labels.includes('agent:approved');
  const changesRequested = labels.includes('agent:changes-requested');
  if (approved === changesRequested) return null;
  return approved ? 'approved' : 'changes-requested';
}

// Pure evaluator used by the no-network regression test and by the GitHub-backed client below.
export function evaluateApprovalEvidence(pr, evidence) {
  const expectedTitle = verdictTitle(pr);
  const lastEditedAt = time(evidence.lastEditedAt);
  const candidates = [];

  for (const comment of evidence.comments ?? []) {
    for (const runId of markerRunIds(comment)) {
      const run = evidence.runsById?.get(runId);
      const items = evidence.manifestsByRunId?.get(runId);
      if (!run || !items) continue;
      if (run.displayTitle !== expectedTitle || !sourceIsTrusted(run, pr)) continue;
      if (String(run.status).toLowerCase() !== 'completed' || String(run.conclusion).toLowerCase() !== 'success') continue;
      // A body edit during or after the review invalidates it even though the Git head is unchanged.
      if (lastEditedAt > time(run.createdAt)) continue;
      const outcome = manifestOutcome(items, comment, pr);
      if (!outcome) continue;
      candidates.push({ runId, run, outcome });
    }
  }

  candidates.sort((left, right) => time(right.run.createdAt) - time(left.run.createdAt));
  const candidate = candidates[0];
  if (!candidate) {
    return { ok: false, why: 'no approval-specific proof covers the current head, base and body revision' };
  }

  // A newer current-policy run may be in flight, failed, or may have requested changes without a
  // durable comment. Never revive the older approval while that newer verdict is unresolved.
  const newer = (evidence.recentRuns ?? []).find((run) =>
    Number(run.databaseId) !== candidate.runId &&
    run.displayTitle === expectedTitle && sourceIsTrusted(run, pr) &&
    time(run.createdAt) > time(candidate.run.createdAt));
  if (newer) {
    return { ok: false, why: `newer verdict run ${newer.databaseId} supersedes approval run ${candidate.runId}` };
  }

  if (candidate.outcome !== 'approved') {
    return { ok: false, why: `latest attested verdict run ${candidate.runId} requested changes` };
  }
  return { ok: true, runId: candidate.runId };
}

function findManifestFiles(root) {
  const found = [];
  for (const entry of readdirSync(root)) {
    const path = join(root, entry);
    if (statSync(path).isDirectory()) found.push(...findManifestFiles(path));
    else if (entry === 'safe-output-items.jsonl') found.push(path);
  }
  return found;
}

export function createApprovalProofClient({ gh, runGh }) {
  let repoSlug;
  const runCache = new Map();
  const manifestCache = new Map();

  const repository = () => {
    repoSlug ??= JSON.parse(gh(['repo', 'view', '--json', 'nameWithOwner'])).nameWithOwner;
    return repoSlug;
  };

  const runFor = (runId) => {
    if (!runCache.has(runId)) {
      runCache.set(runId, JSON.parse(gh(['run', 'view', String(runId), '--json',
        'databaseId,displayTitle,event,headBranch,status,conclusion,createdAt'])));
    }
    return runCache.get(runId);
  };

  const manifestFor = (runId) => {
    if (manifestCache.has(runId)) return manifestCache.get(runId);
    const scratch = mkdtempSync(join(tmpdir(), `plenipo-approval-${runId}-`));
    try {
      const result = runGh(['run', 'download', String(runId), '--name', 'safe-outputs-items', '--dir', scratch]);
      if (result.error || result.status !== 0) {
        manifestCache.set(runId, null);
        return null;
      }
      const file = findManifestFiles(scratch)[0];
      if (!file) {
        manifestCache.set(runId, null);
        return null;
      }
      const items = readFileSync(file, 'utf8').split('\n').filter(Boolean).map((line) => JSON.parse(line));
      manifestCache.set(runId, items);
      return items;
    } catch {
      manifestCache.set(runId, null);
      return null;
    } finally {
      rmSync(scratch, { recursive: true, force: true });
    }
  };

  return {
    prove(pr) {
      try {
        const [owner, name] = repository().split('/');
        const pages = JSON.parse(gh(['api', '--paginate', '--slurp',
          `repos/${owner}/${name}/issues/${pr.number}/comments?per_page=100`]));
        const comments = pages.flat();
        const runIds = [...new Set(comments.flatMap(markerRunIds))].slice(-20);
        const runsById = new Map();
        const manifestsByRunId = new Map();
        for (const runId of runIds) {
          try {
            runsById.set(runId, runFor(runId));
            const manifest = manifestFor(runId);
            if (manifest) manifestsByRunId.set(runId, manifest);
          } catch {
            // A deleted/expired/unreadable run is simply not approval evidence. A newer reviewer
            // run can recover it; do not let one old comment crash the whole merge queue.
          }
        }

        const editQuery = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){lastEditedAt}}}';
        const editData = JSON.parse(gh(['api', 'graphql', '-f', `query=${editQuery}`,
          '-F', `owner=${owner}`, '-F', `name=${name}`, '-F', `number=${pr.number}`]));
        const lastEditedAt = editData.data?.repository?.pullRequest?.lastEditedAt ?? null;
        const recentRuns = JSON.parse(gh(['run', 'list', '--workflow', VERDICT_WORKFLOW, '--limit', '100', '--json',
          'databaseId,displayTitle,event,headBranch,status,conclusion,createdAt']));

        return evaluateApprovalEvidence(pr, { comments, runsById, manifestsByRunId, lastEditedAt, recentRuns });
      } catch (error) {
        return {
          ok: false,
          infrastructure: true,
          why: `could not verify approval provenance: ${error.message.split('\n')[0]}`,
        };
      }
    },
  };
}
