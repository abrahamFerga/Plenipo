import { useQuery } from "@tanstack/react-query";
import { api, type AiDecision } from "../lib/api";

/**
 * The account-level ADMT disclosure page (`/account/ai-decisions`): every AI-originated action in
 * the caller's tenant, grouped by day, each entry carrying a plain-language summary and the
 * human-oversight outcome — approved by whom, rejected, or ran automatically because the tool is
 * not approval-gated. This is the consumer-facing answer an automated-decision-disclosure regime
 * (e.g. California's CPPA ADMT rules) expects, so the Download button exports exactly the records
 * shown, as JSON a user could attach to an ADMT request. Oversight badges are never color-only:
 * icon + text + color, the same redundant-coding rule the rest of the kit follows.
 */
export function AiDecisionLog() {
  const decisions = useQuery({ queryKey: ["ai-decisions"], queryFn: () => api.aiDecisions.list() });

  if (decisions.isPending) {
    return <p className="text-sm text-slate-500 dark:text-slate-400">Loading AI decision history…</p>;
  }
  if (decisions.isError) {
    return (
      <p className="text-sm text-red-600 dark:text-red-400">Could not load your AI decision history.</p>
    );
  }

  const byDay = groupByDay(decisions.data);

  return (
    <div className="max-w-2xl">
      <div className="mb-4 flex items-start justify-between gap-4">
        <div>
          <h2 className="mb-1 text-lg font-semibold text-slate-900 dark:text-slate-100">
            AI decision history
          </h2>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Every action the assistant took or proposed in your workspace — what it was, when, on
            what basis, and the human decision applied. Download this disclosure to attach to an
            automated-decision (ADMT) request.
          </p>
        </div>
        <button
          type="button"
          disabled={decisions.data.length === 0}
          onClick={() => downloadDisclosure(decisions.data)}
          className="focus-ring shrink-0 rounded border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-40 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          Download
        </button>
      </div>

      {decisions.data.length === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-300 p-6 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
          No AI-originated actions have been recorded yet. When the assistant does something — or
          asks permission to — it will be disclosed here.
        </p>
      ) : (
        <div className="space-y-5">
          {byDay.map(([day, entries]) => (
            <section key={day} aria-label={day}>
              <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400 dark:text-slate-500">
                {day}
              </h3>
              <ul className="space-y-2">
                {entries.map((d) => (
                  <DecisionRow key={d.id} decision={d} />
                ))}
              </ul>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

function DecisionRow({ decision }: { decision: AiDecision }) {
  const title = decision.toolDescription ?? decision.toolName;
  const time = new Date(decision.occurredAt).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });

  return (
    <li
      data-testid="ai-decision-entry"
      className="rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900"
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-medium text-slate-900 dark:text-slate-100">
            {decision.toolDescription ?? <span className="font-mono">{decision.toolName}</span>}
          </p>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            {time}
            {decision.moduleName ? ` · ${decision.moduleName}` : ""}
            {decision.requestedBy ? ` · requested via ${decision.requestedBy}'s conversation` : ""}
          </p>
          {decision.summary !== title && (
            <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{decision.summary}</p>
          )}
          {decision.basis && (
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">Why: {decision.basis}</p>
          )}
          {decision.error && (
            <p className="mt-1 text-xs text-red-600 dark:text-red-400">
              Did not complete: {decision.error}
            </p>
          )}
        </div>
        <OversightBadge decision={decision} />
      </div>
    </li>
  );
}

/**
 * The human-oversight outcome, redundantly coded (icon + text + color — never color alone):
 * emerald check "Approved by …", red cross "Rejected", slate bolt "Automatic" for tools that ran
 * without a gate. The badge is the load-bearing disclosure fact, so it names the person when the
 * record has one.
 */
function OversightBadge({ decision }: { decision: AiDecision }) {
  const styles = {
    approved:
      "bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300",
    rejected: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
    automatic: "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300",
  }[decision.oversight];

  const label = {
    approved: decision.decidedBy ? `Approved by ${decision.decidedBy}` : "Approved",
    rejected: decision.decidedBy ? `Rejected by ${decision.decidedBy}` : "Rejected",
    automatic: "Automatic",
  }[decision.oversight];

  const iconPath = {
    approved: "M4 10.5l4 4 8-9", // check
    rejected: "M5 5l10 10M15 5L5 15", // cross
    automatic: "M11 2L4 12h5l-1 6 7-10h-5l1-6", // bolt
  }[decision.oversight];

  return (
    <span
      title={
        decision.oversight === "automatic"
          ? "Ran without prior approval — this tool is not approval-gated"
          : undefined
      }
      className={`inline-flex shrink-0 items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium ${styles}`}
    >
      <svg
        viewBox="0 0 20 20"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
        className="h-3 w-3"
        aria-hidden="true"
      >
        <path d={iconPath} />
      </svg>
      {label}
    </span>
  );
}

/** Recent-first entries bucketed under human-readable day headings (server order is preserved). */
function groupByDay(decisions: AiDecision[]): [string, AiDecision[]][] {
  const groups: [string, AiDecision[]][] = [];
  for (const d of decisions) {
    const day = new Date(d.occurredAt).toLocaleDateString(undefined, {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });
    const last = groups[groups.length - 1];
    if (last && last[0] === day) {
      last[1].push(d);
    } else {
      groups.push([day, [d]]);
    }
  }
  return groups;
}

/**
 * Client-side export of exactly the records shown — no second endpoint, so the disclosure a user
 * downloads can never disagree with the disclosure they read. JSON rather than CSV: the records
 * are heterogeneous (nullable oversight fields, free-text summaries) and the point is a lossless,
 * machine-verifiable artifact to attach to an ADMT request, not a spreadsheet.
 */
function downloadDisclosure(decisions: AiDecision[]) {
  const payload = {
    disclosure: "ai-decision-history",
    exportedAt: new Date().toISOString(),
    count: decisions.length,
    decisions,
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `ai-decisions-${new Date().toISOString().slice(0, 10)}.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}
