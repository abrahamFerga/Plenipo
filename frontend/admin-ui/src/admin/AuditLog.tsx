import { useQuery } from "@tanstack/react-query";
import { api, type AuthEvent } from "@plenipo/ui";

function formatTime(iso: string): string {
  return new Date(iso).toLocaleString();
}

/** Colour an auth-event badge by severity/kind. */
function eventBadgeClass(eventType: string): string {
  const base = "rounded-full px-2 py-0.5 text-xs";
  switch (eventType) {
    case "AccessDenied":
      return `${base} bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-200`;
    case "PermissionRevoked":
      return `${base} bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-200`;
    case "PermissionGranted":
    case "RolePermissionsChanged":
      return `${base} bg-brand-100 text-brand-800 dark:bg-brand-900/40 dark:text-brand-200`;
    case "UserProvisioned":
      return `${base} bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200`;
    default:
      return `${base} bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300`;
  }
}

function ToolCallsTable() {
  const calls = useQuery({
    queryKey: ["admin", "audit", "tool-calls"],
    queryFn: () => api.admin.auditToolCalls(100),
  });

  if (calls.isLoading) {
    return <p className="text-sm text-slate-500">Loading tool calls…</p>;
  }
  if (calls.isError) {
    return <p className="text-sm text-red-600">{(calls.error as Error).message}</p>;
  }

  const rows = calls.data ?? [];

  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
      <table className="w-full text-left text-sm">
        <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
          <tr>
            <th className="px-4 py-2 font-medium">When</th>
            <th className="px-4 py-2 font-medium">User</th>
            <th className="px-4 py-2 font-medium">Module</th>
            <th className="px-4 py-2 font-medium">Tool</th>
            <th className="px-4 py-2 font-medium">Result</th>
            <th className="px-4 py-2 text-right font-medium">ms</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {rows.length === 0 && (
            <tr>
              <td colSpan={6} className="px-4 py-6 text-center text-slate-400">
                No tool calls recorded yet.
              </td>
            </tr>
          )}
          {rows.map((c) => (
            <tr key={c.id}>
              <td className="px-4 py-2 text-slate-500 dark:text-slate-400">{formatTime(c.occurredAt)}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300">{c.userDisplay ?? "—"}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300">{c.moduleId}</td>
              <td className="px-4 py-2 font-mono text-xs text-slate-800 dark:text-slate-200">{c.toolName}</td>
              <td className="px-4 py-2">
                {c.success ? (
                  <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-xs text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200">
                    ok
                  </span>
                ) : (
                  <span
                    className="rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-800 dark:bg-red-900/40 dark:text-red-200"
                    title={c.error ?? undefined}
                  >
                    failed
                  </span>
                )}
              </td>
              <td className="px-4 py-2 text-right font-mono text-xs text-slate-500">{c.durationMs}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function AuthEventsTable() {
  const events = useQuery({
    queryKey: ["admin", "audit", "auth-events"],
    queryFn: () => api.admin.auditAuthEvents(100),
  });

  if (events.isLoading) {
    return <p className="text-sm text-slate-500">Loading access events…</p>;
  }
  if (events.isError) {
    return <p className="text-sm text-red-600">{(events.error as Error).message}</p>;
  }

  const rows: AuthEvent[] = events.data ?? [];

  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
      <table className="w-full text-left text-sm">
        <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
          <tr>
            <th className="px-4 py-2 font-medium">When</th>
            <th className="px-4 py-2 font-medium">User</th>
            <th className="px-4 py-2 font-medium">Event</th>
            <th className="px-4 py-2 font-medium">Detail</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {rows.length === 0 && (
            <tr>
              <td colSpan={4} className="px-4 py-6 text-center text-slate-400">
                No access or security events recorded yet.
              </td>
            </tr>
          )}
          {rows.map((e) => (
            <tr key={e.id}>
              <td className="px-4 py-2 text-slate-500 dark:text-slate-400">{formatTime(e.occurredAt)}</td>
              <td className="px-4 py-2 text-slate-700 dark:text-slate-300" title={e.subject ?? undefined}>
                {e.userDisplay ?? e.subject ?? "—"}
              </td>
              <td className="px-4 py-2">
                <span className={eventBadgeClass(e.eventType)}>{e.eventType}</span>
              </td>
              <td className="px-4 py-2 text-slate-600 dark:text-slate-300">{e.detail ?? "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The audit log: the "audit everything" guarantee surfaced for review — every tool the agent invoked
 * (who, which permission authorized it, the outcome) plus the identity/authorization events (sign-ins,
 * user provisioning, permission grants/revokes, and role-baseline changes).
 */
export function AuditLog() {
  return (
    <div className="space-y-8">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Audit Log</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          The most recent activity for this tenant, from the append-only audit store.
        </p>
      </header>

      <section className="space-y-3">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">Agent tool calls</h2>
        <ToolCallsTable />
      </section>

      <section className="space-y-3">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">
          Access &amp; security events
        </h2>
        <AuthEventsTable />
      </section>
    </div>
  );
}
