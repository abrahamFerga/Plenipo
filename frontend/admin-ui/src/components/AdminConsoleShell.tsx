import { ThemeToggle, useMe } from "@plenipo/ui";
import { AdminPage } from "../admin/AdminPage";

/** True if the caller holds any platform-administration permission (or the global wildcard). */
function canAdminister(permissions: string[]): boolean {
  return permissions.some(
    (p) => p === "*" || p === "platform.*" || p.startsWith("platform."),
  );
}

const WORKSPACE_URL = import.meta.env.VITE_WORKSPACE_URL ?? "/";

/**
 * The admin console's chrome: a slim top bar plus the administration surface. This is the
 * host-independent operator UI (security, roles/users, token usage, audit) — the domain UI is a
 * separate app. The console is served at /admin (by the Vite dev server, or by the API host via
 * UsePlenipoAdminConsole); the /api/admin endpoints it reads remain RBAC-gated server-side.
 */
export function AdminConsoleShell() {
  const { data: me, isLoading, isError } = useMe();
  const authorized = canAdminister(me?.permissions ?? []);

  return (
    <div className="flex h-full flex-col bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100">
      {authorized && (
        <a
          href="#main-content"
          className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-brand-600 focus:px-3 focus:py-2 focus:text-sm focus:font-medium focus:text-white"
        >
          Skip to content
        </a>
      )}
      <header className="flex h-14 items-center gap-4 border-b border-slate-200 bg-white px-4 dark:border-slate-700 dark:bg-slate-900">
        <div className="flex items-center gap-2">
          <div className="flex h-7 w-7 items-center justify-center rounded-md bg-brand-600 text-sm font-bold text-white">
            C
          </div>
          <span className="text-lg font-semibold tracking-tight text-slate-900 dark:text-slate-100">
            Plenipo
          </span>
          <span className="rounded bg-slate-100 px-2 py-0.5 text-xs font-medium uppercase tracking-wide text-slate-500 dark:bg-slate-800 dark:text-slate-400">
            Admin
          </span>
        </div>

        <a
          href={WORKSPACE_URL}
          className="focus-ring rounded text-sm font-medium text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100"
        >
          ← Workspace
        </a>

        <div className="ml-auto flex items-center gap-2">
          <ThemeToggle />
          <div className="text-sm text-slate-600 dark:text-slate-300">
            {me?.displayName ?? "…"}
          </div>
        </div>
      </header>

      {isLoading ? (
        <div role="status" className="grid flex-1 place-items-center text-sm text-slate-500">
          Loading…
        </div>
      ) : isError ? (
        <div role="alert" className="grid flex-1 place-items-center p-6 text-center text-sm text-red-600">
          Could not reach the Plenipo API. Check that it is running and that VITE_API_BASE points at it.
        </div>
      ) : authorized ? (
        <AdminPage />
      ) : (
        <div role="status" className="grid flex-1 place-items-center p-6 text-center text-sm text-slate-500">
          You do not have permission to administer this Plenipo instance.
        </div>
      )}
    </div>
  );
}
