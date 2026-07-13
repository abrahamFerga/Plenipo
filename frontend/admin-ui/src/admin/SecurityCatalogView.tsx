import { useQuery } from "@tanstack/react-query";
import { api, hasPermission, type PermissionInfo, type RoleInfo } from "@plenipo/ui";

function PermissionRow({ p, roles }: { p: PermissionInfo; roles: RoleInfo[] }) {
  const grantedTo = roles.filter((r) => hasPermission(r.permissions, p.permission));
  return (
    <tr>
      <td className="px-4 py-2 font-mono text-xs text-brand-700 dark:text-brand-300">
        {p.permission}
      </td>
      <td className="px-4 py-2 text-slate-700 dark:text-slate-300">{p.description}</td>
      <td className="px-4 py-2">
        <div className="flex flex-wrap gap-1">
          {grantedTo.length === 0 ? (
            <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500 dark:bg-slate-800 dark:text-slate-400">
              no role
            </span>
          ) : (
            grantedTo.map((r) => (
              <span
                key={r.role}
                className="rounded-full bg-brand-50 px-2 py-0.5 text-xs text-brand-700 dark:bg-brand-900/30 dark:text-brand-200"
              >
                {r.role}
              </span>
            ))
          )}
        </div>
      </td>
      <td className="px-4 py-2">
        <div className="flex flex-wrap gap-1">
          {p.requiresApproval && (
            <span className="rounded-full bg-amber-100 px-2 py-0.5 text-xs text-amber-800 dark:bg-amber-900/40 dark:text-amber-200">
              approval
            </span>
          )}
          {p.audited && (
            <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-xs text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200">
              audited
            </span>
          )}
        </div>
      </td>
    </tr>
  );
}

function PermTable({ rows, roles }: { rows: PermissionInfo[]; roles: RoleInfo[] }) {
  return (
    <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
      <table className="w-full text-left text-sm">
        <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
          <tr>
            <th className="px-4 py-2 font-medium">Permission</th>
            <th className="px-4 py-2 font-medium">Description</th>
            <th className="px-4 py-2 font-medium">Granted to</th>
            <th className="px-4 py-2 font-medium">Flags</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
          {rows.map((p) => (
            <PermissionRow key={p.permission} p={p} roles={roles} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The permission reference: every permission the RBAC system can grant — platform permissions
 * plus, for each installed module, the tools the agent can call and the permission each one
 * requires, with the roles that currently grant it. Rendered as a collapsible reference INSIDE
 * the Roles page (it's documentation for the editor above it, not a destination of its own).
 */
export function SecurityCatalogView() {
  const catalog = useQuery({ queryKey: ["admin", "security"], queryFn: api.admin.securityCatalog });
  const roles = useQuery({ queryKey: ["admin", "roles"], queryFn: api.admin.roles });

  if (catalog.isLoading || roles.isLoading) {
    return <p className="text-sm text-slate-500">Loading security configuration…</p>;
  }
  if (catalog.isError) {
    return <p className="text-sm text-red-600">{(catalog.error as Error).message}</p>;
  }

  const roleRows = roles.data ?? [];

  return (
    <div className="space-y-8">
      <p className="text-sm text-slate-500 dark:text-slate-400">
        What each permission allows and which roles currently grant it in this tenant (wildcards
        resolved). Edit grants with the role editor above, or per user on <strong>Users</strong>. The
        agent never receives the schema of a tool the caller lacks permission to call.
      </p>

      <section className="space-y-3">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">
          Platform permissions
        </h2>
        <PermTable rows={catalog.data!.platform} roles={roleRows} />
      </section>

      {catalog.data!.modules.map((m) => (
        <section key={m.id} className="space-y-3">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-slate-400">
            {m.displayName} — agent tools
          </h2>
          {m.tools.length === 0 ? (
            <p className="text-sm text-slate-400">This module exposes no agent tools.</p>
          ) : (
            <PermTable rows={m.tools} roles={roleRows} />
          )}
        </section>
      ))}
    </div>
  );
}
