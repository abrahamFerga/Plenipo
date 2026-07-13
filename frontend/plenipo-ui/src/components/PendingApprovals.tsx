import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type PendingApproval } from "../lib/api";
import { useMe } from "../hooks/useMe";
import { hasPermission } from "../lib/permissions";

/**
 * The recorded tool arguments, split for rendering: three keys carry review-surface conventions —
 * `reasoning` (the agent's stated why, shown as prose), and `before`/`after` (object pair, shown
 * as a field-by-field diff) — everything else stays a plain argument list. The conventions cost
 * a module nothing to adopt: declare the parameter, the agent fills it, the card explains itself.
 */
interface ParsedArguments {
  reasoning: string | null;
  diff: { field: string; before: string; after: string }[] | null;
  rest: [string, unknown][];
}

function parseArguments(argumentsJson?: string): ParsedArguments {
  const none: ParsedArguments = { reasoning: null, diff: null, rest: [] };
  if (!argumentsJson) return none;
  try {
    const obj = JSON.parse(argumentsJson) as Record<string, unknown>;
    const reasoning = typeof obj.reasoning === "string" && obj.reasoning.length > 0 ? obj.reasoning : null;
    const before = obj.before, after = obj.after;
    const isRecord = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
    const diff =
      isRecord(before) && isRecord(after)
        ? [...new Set([...Object.keys(before), ...Object.keys(after)])].map((field) => ({
            field,
            before: before[field] == null ? "—" : String(before[field]),
            after: after[field] == null ? "—" : String(after[field]),
          }))
        : null;
    const rest = Object.entries(obj).filter(
      ([k]) => k !== "reasoning" && (diff === null || (k !== "before" && k !== "after")),
    );
    return { reasoning, diff, rest };
  } catch {
    return { ...none, rest: [["arguments", argumentsJson]] };
  }
}

const formatRest = (rest: [string, unknown][]) => rest.map(([k, v]) => `${k}: ${String(v)}`).join(", ");

/**
 * The human-in-the-loop surface inside the chat: lists side-effecting tool calls the agent was
 * blocked from auto-running for this module, and lets the user Approve (which re-executes that
 * exact call on the server) or Reject. Ceremony follows the declaring tool's risk tier — uniform
 * ceremony trains reviewers to rubber-stamp, so routine low-risk calls collapse to a one-tap
 * confirm row while consequential ones keep the full card, with the agent's stated reasoning and
 * a before/after diff whenever the call carries them. Renders nothing when there's nothing pending.
 */
export function PendingApprovals({ moduleId }: { moduleId: string }) {
  const qc = useQueryClient();
  const { data: me } = useMe();
  const canManage = hasPermission(me?.permissions ?? [], "chat.approvals.manage");
  const { data } = useQuery({
    queryKey: ["approvals"],
    queryFn: api.approvals.list,
    enabled: canManage, // only users who can approve fetch the list (the API enforces this too)
  });

  const invalidate = () => qc.invalidateQueries({ queryKey: ["approvals"] });
  const approve = useMutation({ mutationFn: (id: string) => api.approvals.approve(id), onSuccess: invalidate });
  const reject = useMutation({ mutationFn: (id: string) => api.approvals.reject(id), onSuccess: invalidate });

  const pending = (data ?? []).filter((a: PendingApproval) => a.moduleId === moduleId);
  if (pending.length === 0) {
    return null;
  }

  const busy = approve.isPending || reject.isPending;

  return (
    <div className="mb-3 space-y-2">
      {pending.map((a) => {
        const parsed = parseArguments(a.argumentsJson);
        const title = a.description ?? a.toolName;

        if (a.risk === "low") {
          return (
            <div
              key={a.id}
              data-testid="approval-compact"
              className="flex items-center gap-3 rounded-lg border border-slate-200 bg-white px-3 py-2 dark:border-slate-700 dark:bg-slate-900"
            >
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm text-slate-700 dark:text-slate-200">{title}</p>
                {(parsed.reasoning ?? formatRest(parsed.rest)) && (
                  <p className="truncate text-xs text-slate-400 dark:text-slate-500">
                    {parsed.reasoning ?? formatRest(parsed.rest)}
                  </p>
                )}
              </div>
              <button
                type="button"
                disabled={busy}
                onClick={() => approve.mutate(a.id)}
                className="focus-ring shrink-0 rounded-md bg-emerald-600 px-3 py-1 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
              >
                Approve
              </button>
              <button
                type="button"
                disabled={busy}
                onClick={() => reject.mutate(a.id)}
                aria-label={`Reject ${title}`}
                className="focus-ring shrink-0 rounded-md px-1.5 py-1 text-xs text-slate-400 hover:bg-slate-100 dark:hover:bg-slate-800"
              >
                ✕
              </button>
            </div>
          );
        }

        return (
          <div
            key={a.id}
            data-testid="approval-card"
            className="rounded-lg border border-amber-300 bg-amber-50 p-3 dark:border-amber-700/60 dark:bg-amber-900/20"
          >
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="text-sm font-medium text-amber-900 dark:text-amber-200">
                  Approval required: {a.description ?? <span className="font-mono">{a.toolName}</span>}
                </p>
                {parsed.reasoning && (
                  <p className="mt-1 text-xs text-amber-800/90 dark:text-amber-300/90" data-testid="approval-reasoning">
                    Why: {parsed.reasoning}
                  </p>
                )}
                {parsed.diff && (
                  <dl className="mt-1.5 space-y-0.5 text-xs" data-testid="approval-diff">
                    {parsed.diff.map((d) => (
                      <div key={d.field} className="flex flex-wrap gap-x-1.5">
                        <dt className="font-medium text-amber-900 dark:text-amber-200">{d.field}:</dt>
                        <dd className="text-amber-800/80 line-through dark:text-amber-300/70">{d.before}</dd>
                        <dd aria-hidden className="text-amber-800/60 dark:text-amber-300/60">→</dd>
                        <dd className="font-medium text-amber-900 dark:text-amber-100">{d.after}</dd>
                      </div>
                    ))}
                  </dl>
                )}
                {parsed.rest.length > 0 && (
                  <p className="mt-0.5 truncate text-xs text-amber-800/80 dark:text-amber-300/80">
                    {formatRest(parsed.rest)}
                  </p>
                )}
              </div>
              <div className="flex shrink-0 gap-2">
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => approve.mutate(a.id)}
                  className="focus-ring rounded-md bg-emerald-600 px-3 py-1 text-xs font-medium text-white hover:bg-emerald-500 disabled:opacity-50"
                >
                  Approve
                </button>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => reject.mutate(a.id)}
                  className="focus-ring rounded-md border border-slate-300 px-3 py-1 text-xs font-medium text-slate-600 hover:bg-slate-100 disabled:opacity-50 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800"
                >
                  Reject
                </button>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}
