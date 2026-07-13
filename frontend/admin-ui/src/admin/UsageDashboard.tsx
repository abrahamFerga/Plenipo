import { useQuery } from "@tanstack/react-query";
import { api } from "@abrahamferga/cortex-ui";

function formatTokens(n: number): string {
  return new Intl.NumberFormat("en-US").format(n);
}

function StatCard({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-slate-900 dark:text-slate-100">{value}</p>
    </div>
  );
}

/** Bar list: a simple horizontal bar per row, scaled to the max value. */
function BarList({ rows }: { rows: { label: string; value: number }[] }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <div className="space-y-2">
      {rows.map((r) => (
        <div key={r.label} className="flex items-center gap-3 text-sm">
          <span className="w-28 shrink-0 truncate text-slate-600 dark:text-slate-300">{r.label}</span>
          <div className="h-4 flex-1 overflow-hidden rounded bg-slate-100 dark:bg-slate-800">
            <div
              className="h-full rounded bg-brand-500"
              style={{ width: `${(r.value / max) * 100}%` }}
            />
          </div>
          <span className="w-20 shrink-0 text-right font-mono text-xs text-slate-500">
            {formatTokens(r.value)}
          </span>
        </div>
      ))}
    </div>
  );
}

/**
 * Token-usage dashboard. Aggregates the per-turn usage records captured by the
 * agent runner (the MAF token-usage pattern) into totals, a per-module split,
 * and a daily series — the basis for cost monitoring per tenant.
 */
export function UsageDashboard() {
  const usage = useQuery({ queryKey: ["admin", "usage"], queryFn: () => api.admin.usage(30) });

  if (usage.isLoading) {
    return <p className="text-sm text-slate-500">Loading usage…</p>;
  }
  if (usage.isError) {
    return <p className="text-sm text-red-600">{(usage.error as Error).message}</p>;
  }

  const u = usage.data!;

  return (
    <div className="space-y-6">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Token Usage</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">Last 30 days, this tenant.</p>
      </header>

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard label="Total tokens" value={formatTokens(u.totalTokens)} />
        <StatCard label="Input" value={formatTokens(u.inputTokens)} />
        <StatCard label="Output" value={formatTokens(u.outputTokens)} />
        <StatCard label="Turns" value={formatTokens(u.turns)} />
      </div>

      {u.turns === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
          No agent activity recorded yet. Token usage appears here after the first chat turn.
        </p>
      ) : (
        <>
          <section className="space-y-3">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">By module</h2>
            <BarList rows={u.byModule.map((m) => ({ label: m.moduleId, value: m.totalTokens }))} />
          </section>

          <section className="space-y-3">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">By day</h2>
            <BarList rows={u.byDay.map((d) => ({ label: d.day, value: d.totalTokens }))} />
          </section>
        </>
      )}
    </div>
  );
}
