import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type ModuleAdmin } from "@plenipo/ui";

function Toggle({ on, disabled, onChange }: { on: boolean; disabled: boolean; onChange: (next: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={on}
      disabled={disabled}
      onClick={() => onChange(!on)}
      className={`focus-ring relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors disabled:opacity-50 ${
        on ? "bg-brand-600" : "bg-slate-300 dark:bg-slate-600"
      }`}
    >
      <span
        className={`inline-block h-5 w-5 transform rounded-full bg-white shadow transition-transform ${
          on ? "translate-x-5" : "translate-x-0.5"
        }`}
      />
    </button>
  );
}

function ModuleRow({ module }: { module: ModuleAdmin }) {
  const qc = useQueryClient();
  const toggle = useMutation({
    mutationFn: (enabled: boolean) => api.admin.setModuleEnabled(module.id, enabled),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "modules"] }),
  });

  return (
    <div className="flex items-center justify-between gap-4 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <div className="min-w-0">
        <p className="flex items-center gap-2 font-medium text-slate-900 dark:text-slate-100">
          {module.displayName}
          <span className="font-mono text-xs text-slate-400">{module.id}</span>
          {!module.enabled && (
            <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500 dark:bg-slate-800">
              disabled
            </span>
          )}
        </p>
        {module.description && (
          <p className="mt-0.5 truncate text-sm text-slate-500 dark:text-slate-400">{module.description}</p>
        )}
      </div>
      <Toggle on={module.enabled} disabled={toggle.isPending} onChange={(next) => toggle.mutate(next)} />
    </div>
  );
}

/**
 * Per-tenant module management: enable or disable the installed domain modules for this tenant. A disabled
 * module disappears from the workspace navigation (it's filtered out of GET /api/platform/modules). The
 * permission model still independently governs each module's tools.
 */
export function ModulesAdmin() {
  const modules = useQuery({ queryKey: ["admin", "modules"], queryFn: api.admin.modules });

  if (modules.isLoading) {
    return <p className="text-sm text-slate-500">Loading modules…</p>;
  }
  if (modules.isError) {
    return <p className="text-sm text-red-600">{(modules.error as Error).message}</p>;
  }

  const rows = modules.data ?? [];

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Modules</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          The per-tenant licensing switch: which of this deployment's installed domain modules this tenant may
          use. Enabling one gives the tenant its workspace tabs and its agent; disabling removes them (its
          tools become uninvocable, not just hidden). Changes are recorded in the audit trail.
        </p>
      </header>

      {rows.length === 0 ? (
        <div className="space-y-1 rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
          <p className="font-medium text-slate-500 dark:text-slate-300">No modules are installed in this deployment.</p>
          <p>
            Modules are installed in code by the product host (<code className="font-mono text-xs">AddPlenipoModule&lt;T&gt;()</code>)
            — this page only toggles them per tenant. This deployment's host installs modules in code
            (AddPlenipoModule) — none are installed here yet.
          </p>
        </div>
      ) : (
        <div className="space-y-2">
          {rows.map((m) => (
            <ModuleRow key={m.id} module={m} />
          ))}
        </div>
      )}
    </div>
  );
}
