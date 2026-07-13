import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ConfirmDialog, type AdminUser } from "@plenipo/ui";
import { InvitesPanel } from "./InvitesPanel";

function Chip({ label, onRemove }: { label: string; onRemove: () => void }) {
  return (
    <span className="inline-flex items-center gap-1 rounded bg-slate-100 px-2 py-0.5 font-mono text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-300">
      {label}
      <button
        type="button"
        onClick={onRemove}
        className="focus-ring rounded text-slate-400 hover:text-red-600"
        aria-label={`Remove ${label}`}
      >
        ×
      </button>
    </span>
  );
}

function UserCard({ user, allRoles }: { user: AdminUser; allRoles: string[] }) {
  const qc = useQueryClient();
  const [permInput, setPermInput] = useState("");
  const [confirmingDeactivate, setConfirmingDeactivate] = useState(false);
  const invalidate = () => qc.invalidateQueries({ queryKey: ["admin", "users"] });

  // All tool permissions discovered from the security catalog, for quick granting.
  const catalog = useQuery({ queryKey: ["admin", "security"], queryFn: api.admin.securityCatalog });
  const grantable = useMemo(() => {
    const tools = catalog.data?.modules.flatMap((m) => m.tools.map((t) => t.permission)) ?? [];
    return [...new Set(tools)].sort();
  }, [catalog.data]);

  const assignRole = useMutation({ mutationFn: (r: string) => api.admin.assignRole(user.id, r), onSuccess: invalidate });
  const revokeRole = useMutation({ mutationFn: (r: string) => api.admin.revokeRole(user.id, r), onSuccess: invalidate });
  const grantPerm = useMutation({ mutationFn: (p: string) => api.admin.grantPermission(user.id, p), onSuccess: invalidate });
  const revokePerm = useMutation({ mutationFn: (p: string) => api.admin.revokePermission(user.id, p), onSuccess: invalidate });
  const setActive = useMutation({ mutationFn: (active: boolean) => api.admin.setUserActive(user.id, active), onSuccess: invalidate });

  const availableRoles = allRoles.filter((r) => !user.roles.includes(r));

  return (
    <div
      className={`rounded-lg border bg-white p-4 dark:bg-slate-900 ${
        user.isActive ? "border-slate-200 dark:border-slate-700" : "border-red-200 dark:border-red-900/60"
      }`}
    >
      <div className="mb-3 flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="font-medium text-slate-900 dark:text-slate-100">
            {user.displayName ?? user.email}
          </p>
          <p className="text-xs text-slate-500 dark:text-slate-400">{user.email}</p>
          <p className="truncate font-mono text-xs text-slate-400" title={user.subject}>
            {user.subject}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {!user.isActive && (
            <span className="rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700 dark:bg-red-900/40 dark:text-red-200">
              inactive
            </span>
          )}
          <button
            type="button"
            disabled={setActive.isPending}
            onClick={() => (user.isActive ? setConfirmingDeactivate(true) : setActive.mutate(true))}
            className={`focus-ring rounded border px-2 py-0.5 text-xs font-medium disabled:opacity-40 ${
              user.isActive
                ? "border-red-300 text-red-600 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20"
                : "border-emerald-300 text-emerald-700 hover:bg-emerald-50 dark:border-emerald-800 dark:hover:bg-emerald-900/20"
            }`}
          >
            {setActive.isPending ? "…" : user.isActive ? "Deactivate" : "Activate"}
          </button>
        </div>
      </div>
      {setActive.isError && (
        <p className="mb-2 text-xs text-red-600">{(setActive.error as Error).message}</p>
      )}

      <div className="space-y-3">
        <div>
          <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Roles</p>
          <div className="flex flex-wrap items-center gap-1">
            {user.roles.map((r) => (
              <Chip key={r} label={r} onRemove={() => revokeRole.mutate(r)} />
            ))}
            {availableRoles.length > 0 && (
              <select
                aria-label="Add a role"
                className="rounded border border-slate-300 bg-white px-2 py-0.5 text-xs focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
                value=""
                onChange={(e) => e.target.value && assignRole.mutate(e.target.value)}
              >
                <option value="">+ add role</option>
                {availableRoles.map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </select>
            )}
          </div>
        </div>

        <div>
          <p className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">
            Permission grants
          </p>
          <div className="flex flex-wrap items-center gap-1">
            {user.permissions.length === 0 && (
              <span className="text-xs text-slate-400">none</span>
            )}
            {user.permissions.map((p) => (
              <Chip key={p} label={p} onRemove={() => revokePerm.mutate(p)} />
            ))}
          </div>
          <form
            className="mt-2 flex gap-1"
            onSubmit={(e) => {
              e.preventDefault();
              if (permInput.trim()) {
                grantPerm.mutate(permInput.trim());
                setPermInput("");
              }
            }}
          >
            <input
              aria-label="Grant a permission"
              list={`perms-${user.id}`}
              value={permInput}
              onChange={(e) => setPermInput(e.target.value)}
              placeholder="tools.finance.summarize_spending"
              className="flex-1 rounded border border-slate-300 bg-white px-2 py-1 font-mono text-xs focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
            />
            <datalist id={`perms-${user.id}`}>
              {grantable.map((p) => (
                <option key={p} value={p} />
              ))}
            </datalist>
            <button
              type="submit"
              className="focus-ring rounded bg-brand-600 px-3 py-1 text-xs font-medium text-white hover:bg-brand-500"
            >
              Grant
            </button>
          </form>
        </div>
      </div>

      <ConfirmDialog
        open={confirmingDeactivate}
        title="Deactivate user"
        body={`Deactivate ${user.displayName ?? user.email}? They will be denied access until reactivated.`}
        confirmLabel="Deactivate"
        tone="danger"
        onConfirm={() => {
          setConfirmingDeactivate(false);
          setActive.mutate(false);
        }}
        onCancel={() => setConfirmingDeactivate(false)}
      />
    </div>
  );
}

/** Manage users in the current tenant: assign/revoke roles and fine-grained permission grants. */
export function UsersAdmin() {
  const users = useQuery({ queryKey: ["admin", "users"], queryFn: api.admin.users });
  // Source assignable roles from the live role list, so custom (tenant-defined) roles are assignable too.
  const roles = useQuery({ queryKey: ["admin", "roles"], queryFn: api.admin.roles });
  const allRoles = useMemo(() => roles.data?.map((r) => r.role) ?? [], [roles.data]);

  if (users.isLoading) {
    return <p className="text-sm text-slate-500">Loading users…</p>;
  }
  if (users.isError) {
    return <p className="text-sm text-red-600">{(users.error as Error).message}</p>;
  }

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Users & Roles</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Roles grant baseline permissions; per-user grants unlock individual module tools.
        </p>
      </header>

      <InvitesPanel allRoles={allRoles} />

      <div className="grid gap-3 lg:grid-cols-2">
        {users.data?.map((u) => (
          <UserCard key={u.id} user={u} allRoles={allRoles} />
        ))}
      </div>
    </div>
  );
}
