import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, type AgentRun, type AgentRunFilters } from "@plenipo/ui";

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

function formatNumber(n: number): string {
  return new Intl.NumberFormat("en-US").format(n);
}

function formatMs(ms: number | undefined): string {
  if (ms === undefined || ms === null) return "—";
  return ms < 1000 ? `${formatNumber(ms)} ms` : `${(ms / 1000).toFixed(2)} s`;
}

function formatCost(cost: number, currency = "USD"): string {
  return new Intl.NumberFormat("en-US", { style: "currency", currency, maximumFractionDigits: 4 }).format(cost);
}

/**
 * Colour a run by how it ended. Only "Completed" is green — every other outcome is something an
 * operator may need to act on, which is the whole reason these rows exist.
 */
function outcomeBadgeClass(outcome: string): string {
  const base = "rounded-full px-2 py-0.5 text-xs font-medium";
  switch (outcome) {
    case "Completed":
      return `${base} bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200`;
    case "Error":
    case "ProviderUnavailable":
      return `${base} bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-200`;
    case "BlockedBySecurity":
    case "BudgetExceeded":
    case "Rejected":
      return `${base} bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-200`;
    default:
      return `${base} bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300`;
  }
}

function StatCard({ label, value, tone }: { label: string; value: string; tone?: "danger" }) {
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</p>
      <p
        className={`mt-1 text-2xl font-semibold ${
          tone === "danger"
            ? "text-red-600 dark:text-red-400"
            : "text-slate-900 dark:text-slate-100"
        }`}
      >
        {value}
      </p>
    </div>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</dt>
      <dd className="mt-0.5 text-sm text-slate-800 dark:text-slate-200">{children}</dd>
    </div>
  );
}

const selectClass =
  "focus-ring rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-700 dark:border-slate-600 dark:bg-slate-900 dark:text-slate-200";

/** One turn, reconstructed: what ran, how it ended, what it called, and the steps beneath it. */
function RunDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const detail = useQuery({ queryKey: ["admin", "runs", id], queryFn: () => api.admin.run(id) });

  if (detail.isLoading) {
    return <p className="text-sm text-slate-500">Loading run…</p>;
  }
  if (detail.isError) {
    return <p className="text-sm text-red-600">{(detail.error as Error).message}</p>;
  }

  const { run, toolCalls, steps } = detail.data!;

  return (
    <div className="space-y-6">
      <button type="button" onClick={onBack} className="focus-ring text-sm text-brand-600 hover:underline">
        ← Back to runs
      </button>

      <header className="flex flex-wrap items-center gap-3">
        <span className={outcomeBadgeClass(run.outcome)}>{run.outcome}</span>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">
          {run.workflowName ?? run.agentName ?? run.moduleId}
        </h1>
        <span className="text-sm text-slate-500">{formatTime(run.occurredAt)}</span>
      </header>

      {run.errorKind && (
        <div className="rounded-lg border border-red-200 bg-red-50 p-4 dark:border-red-900/50 dark:bg-red-950/30">
          <p className="text-xs font-semibold uppercase tracking-wide text-red-700 dark:text-red-300">
            {run.errorKind}
          </p>
          {run.errorMessage && (
            <p className="mt-1 whitespace-pre-wrap font-mono text-xs text-red-900 dark:text-red-200">
              {run.errorMessage}
            </p>
          )}
        </div>
      )}

      <dl className="grid grid-cols-2 gap-4 rounded-lg border border-slate-200 p-4 dark:border-slate-700 sm:grid-cols-4">
        <Field label="User">{run.userDisplay ?? "—"}</Field>
        <Field label="Module">{run.moduleId}</Field>
        <Field label="Provider">{run.provider ?? "—"}</Field>
        <Field label="Model">{run.model ?? "—"}</Field>
        <Field label="First token">{formatMs(run.firstTokenMs)}</Field>
        <Field label="Total">{formatMs(run.totalMs)}</Field>
        <Field label="Tokens">
          {formatNumber(run.totalTokens)}
          <span className="text-slate-400">
            {" "}
            ({formatNumber(run.inputTokens)} in / {formatNumber(run.outputTokens)} out)
          </span>
        </Field>
        <Field label="Cost">{run.cost != null ? formatCost(run.cost, run.currency) : "—"}</Field>
        <Field label="Approvals blocked">{formatNumber(run.approvalCount)}</Field>
        <Field label="Conversation">
          <span className="font-mono text-xs">{run.conversationId ?? "—"}</span>
        </Field>
        <Field label="Instructions">
          {/* The provenance join: which exact instruction assembly this turn ran under. */}
          <span className="font-mono text-xs">{run.instructionsHash?.slice(0, 12) ?? "—"}</span>
        </Field>
        <Field label="Trace">
          <span className="font-mono text-xs">{run.traceId ?? "—"}</span>
        </Field>
      </dl>

      <section className="space-y-3">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">
          Tool calls ({toolCalls.length})
        </h2>
        {toolCalls.length === 0 ? (
          <p className="rounded-lg border border-dashed border-slate-300 p-6 text-center text-sm text-slate-400 dark:border-slate-700">
            This turn invoked no tools.
          </p>
        ) : (
          <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
            <table className="w-full text-left text-sm">
              <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
                <tr>
                  <th className="px-4 py-2 font-medium">When</th>
                  <th className="px-4 py-2 font-medium">Tool</th>
                  <th className="px-4 py-2 font-medium">Permission</th>
                  <th className="px-4 py-2 font-medium">Result</th>
                  <th className="px-4 py-2 text-right font-medium">ms</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                {toolCalls.map((c) => (
                  <tr key={c.id}>
                    <td className="px-4 py-2 text-slate-500 dark:text-slate-400">{formatTime(c.occurredAt)}</td>
                    <td className="px-4 py-2 font-mono text-xs text-slate-800 dark:text-slate-200">{c.toolName}</td>
                    <td className="px-4 py-2 font-mono text-xs text-slate-500">{c.permission}</td>
                    <td className="px-4 py-2">
                      <span
                        className={
                          c.success
                            ? "rounded-full bg-emerald-100 px-2 py-0.5 text-xs text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200"
                            : "rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-800 dark:bg-red-900/40 dark:text-red-200"
                        }
                        title={c.error ?? undefined}
                      >
                        {c.success ? "ok" : "failed"}
                      </span>
                    </td>
                    <td className="px-4 py-2 text-right font-mono text-xs text-slate-500">{c.durationMs}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      {steps.length > 0 && (
        <section className="space-y-3">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">
            Workflow steps ({steps.length})
          </h2>
          <ul className="space-y-2">
            {steps.map((s, i) => (
              <li
                key={s.id}
                className="flex items-center gap-3 rounded-lg border border-slate-200 px-4 py-2 text-sm dark:border-slate-700"
              >
                <span className="w-6 shrink-0 text-slate-400">{i + 1}</span>
                <span className="flex-1 text-slate-800 dark:text-slate-200">{s.agentName ?? s.moduleId}</span>
                <span className={outcomeBadgeClass(s.outcome)}>{s.outcome}</span>
                <span className="w-20 text-right font-mono text-xs text-slate-500">{formatMs(s.totalMs)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function RunsTable({ runs, onSelect }: { runs: AgentRun[]; onSelect: (run: AgentRun) => void }) {
  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
      <table className="w-full text-left text-sm">
        <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
          <tr>
            <th className="px-4 py-2 font-medium">When</th>
            <th className="px-4 py-2 font-medium">User</th>
            <th className="px-4 py-2 font-medium">Module</th>
            <th className="px-4 py-2 font-medium">Agent</th>
            <th className="px-4 py-2 font-medium">Model</th>
            <th className="px-4 py-2 font-medium">Outcome</th>
            <th className="px-4 py-2 text-right font-medium">Latency</th>
            <th className="px-4 py-2 text-right font-medium">Tokens</th>
            <th className="px-4 py-2 text-right font-medium">Tools</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {runs.length === 0 && (
            <tr>
              <td colSpan={9} className="px-4 py-6 text-center text-slate-400">
                No agent runs in this window.
              </td>
            </tr>
          )}
          {runs.map((r) => (
            <tr
              key={r.id}
              onClick={() => onSelect(r)}
              className="cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50"
            >
              <td className="px-4 py-2 text-slate-500 dark:text-slate-400">{formatTime(r.occurredAt)}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300">{r.userDisplay ?? "—"}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300">{r.moduleId}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300">
                {r.workflowName ? `${r.workflowName} (workflow)` : (r.agentName ?? "—")}
              </td>
              <td className="px-4 py-2 font-mono text-xs text-slate-500">{r.model ?? "—"}</td>
              <td className="px-4 py-2 whitespace-nowrap">
                {/* The cause sits beside the badge rather than in a title: a tooltip would both hide it
                    from anyone scanning the list and override the badge's accessible name. */}
                <span className={outcomeBadgeClass(r.outcome)}>{r.outcome}</span>
                {r.errorKind && (
                  <span className="ml-2 font-mono text-xs text-slate-500">{r.errorKind}</span>
                )}
              </td>
              <td className="px-4 py-2 text-right font-mono text-xs text-slate-500">{formatMs(r.totalMs)}</td>
              <td className="px-4 py-2 text-right font-mono text-xs text-slate-500">
                {r.totalTokens > 0 ? formatNumber(r.totalTokens) : "—"}
              </td>
              <td className="px-4 py-2 text-right font-mono text-xs text-slate-500">{r.toolCallCount}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The run explorer: every agent turn this tenant ran, however it ended. Unlike the token-usage
 * dashboard — which can only show turns a provider actually billed — a refused, blocked, or thrown
 * turn appears here too, which is what makes "why didn't the assistant answer?" an answerable question.
 */
export function RunsExplorer() {
  const [filters, setFilters] = useState<AgentRunFilters>({ days: 7 });
  const [selected, setSelected] = useState<string | null>(null);

  const runs = useQuery({
    queryKey: ["admin", "runs", filters],
    queryFn: () => api.admin.runs(filters),
  });

  if (selected) {
    return <RunDetail id={selected} onBack={() => setSelected(null)} />;
  }

  if (runs.isLoading) {
    return <p className="text-sm text-slate-500">Loading runs…</p>;
  }
  if (runs.isError) {
    return <p className="text-sm text-red-600">{(runs.error as Error).message}</p>;
  }

  const data = runs.data!;
  const { summary } = data;

  function update(patch: Partial<AgentRunFilters>) {
    setFilters((f) => ({ ...f, ...patch }));
  }

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Agent Runs</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          One row per turn — completed, refused, blocked, or failed.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-2">
        <select
          aria-label="Time window"
          className={selectClass}
          value={filters.days ?? 7}
          onChange={(e) => update({ days: Number(e.target.value) })}
        >
          <option value={1}>Last 24 hours</option>
          <option value={7}>Last 7 days</option>
          <option value={30}>Last 30 days</option>
          <option value={90}>Last 90 days</option>
        </select>

        <select
          aria-label="Outcome"
          className={selectClass}
          value={filters.outcome ?? ""}
          onChange={(e) => update({ outcome: e.target.value })}
        >
          <option value="">All outcomes</option>
          {data.outcomes.map((o) => (
            <option key={o} value={o}>
              {o}
            </option>
          ))}
        </select>

        <select
          aria-label="Module"
          className={selectClass}
          value={filters.module ?? ""}
          onChange={(e) => update({ module: e.target.value })}
        >
          <option value="">All modules</option>
          {data.modules.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>

        <select
          aria-label="Model"
          className={selectClass}
          value={filters.model ?? ""}
          onChange={(e) => update({ model: e.target.value })}
        >
          <option value="">All models</option>
          {data.models.map((m) => (
            <option key={m} value={m}>
              {m}
            </option>
          ))}
        </select>
      </div>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-5">
        <StatCard label="Runs" value={formatNumber(summary.total)} />
        <StatCard
          label="Error rate"
          value={`${(summary.errorRate * 100).toFixed(1)}%`}
          tone={summary.errors > 0 ? "danger" : undefined}
        />
        <StatCard label="p50 latency" value={formatMs(summary.p50Ms)} />
        <StatCard label="p95 latency" value={formatMs(summary.p95Ms)} />
        <StatCard label="Tokens" value={formatNumber(summary.totalTokens)} />
      </div>

      <RunsTable runs={data.runs} onSelect={(r) => setSelected(r.id)} />
    </div>
  );
}
