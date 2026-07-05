import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ConfirmDialog, type PermissionInfo, type RoleInfo } from "@cortex/ui";
import { SecurityCatalogView } from "./SecurityCatalogView";

// ─────────────────────────────────────────────────────────────────────────────
// Schema-driven role editor.
//
// Every toggle is derived from the live security catalog (platform permissions + each installed module's
// tools), so installing a module or adding a tool surfaces new permissions here automatically — no change
// to this file. Wildcards and anything else not in the catalog are editable through the "Additional
// grants" escape hatch, mirroring OpenClaw's "form from schema + raw editor" approach.
// ─────────────────────────────────────────────────────────────────────────────

interface PermissionGroup {
  key: string;
  title: string;
  permissions: PermissionInfo[];
}

/** Build the grouped permission universe from the catalog: built-ins by category, then one group per module. */
function usePermissionGroups(): { groups: PermissionGroup[]; catalogPermissions: Set<string>; isLoading: boolean; isError: boolean } {
  const catalog = useQuery({ queryKey: ["admin", "security"], queryFn: api.admin.securityCatalog });

  const { groups, catalogPermissions } = useMemo(() => {
    const data = catalog.data;
    if (!data) {
      return { groups: [] as PermissionGroup[], catalogPermissions: new Set<string>() };
    }

    const byCategory = new Map<string, PermissionInfo[]>();
    for (const p of data.platform) {
      const list = byCategory.get(p.category) ?? [];
      list.push(p);
      byCategory.set(p.category, list);
    }

    const builtinGroups: PermissionGroup[] = [...byCategory.entries()].map(([category, permissions]) => ({
      key: `cat:${category}`,
      title: category,
      permissions,
    }));

    const moduleGroups: PermissionGroup[] = data.modules
      .filter((m) => m.tools.length > 0)
      .map((m) => ({ key: `mod:${m.id}`, title: `${m.displayName} — tools`, permissions: m.tools }));

    const all = [...builtinGroups, ...moduleGroups];
    const catalogPermissions = new Set(all.flatMap((g) => g.permissions.map((p) => p.permission)));
    return { groups: all, catalogPermissions };
  }, [catalog.data]);

  return { groups, catalogPermissions, isLoading: catalog.isLoading, isError: catalog.isError };
}

function setsEqual(a: Set<string>, b: Set<string>): boolean {
  if (a.size !== b.size) return false;
  for (const v of a) if (!b.has(v)) return false;
  return true;
}

function Chip({ label, onRemove }: { label: string; onRemove?: () => void }) {
  return (
    <span className="inline-flex items-center gap-1 rounded bg-slate-100 px-2 py-0.5 font-mono text-xs text-slate-700 dark:bg-slate-800 dark:text-slate-300">
      {label}
      {onRemove && (
        <button type="button" onClick={onRemove} className="focus-ring rounded text-slate-400 hover:text-red-600" aria-label={`Remove ${label}`}>
          ×
        </button>
      )}
    </span>
  );
}

function RoleEditorPanel({
  role,
  groups,
  catalogPermissions,
  onDeleted,
}: {
  role: RoleInfo;
  groups: PermissionGroup[];
  catalogPermissions: Set<string>;
  onDeleted: () => void;
}) {
  const qc = useQueryClient();
  const original = useMemo(() => new Set(role.permissions), [role.permissions]);
  const [draft, setDraft] = useState<Set<string>>(() => new Set(role.permissions));
  const [extraInput, setExtraInput] = useState("");
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  // Re-sync the draft whenever the selected role (or its server state) changes.
  useEffect(() => setDraft(new Set(role.permissions)), [role.role, role.permissions]);

  const save = useMutation({
    mutationFn: () => api.admin.setRolePermissions(role.role, [...draft]),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "roles"] }),
  });

  const del = useMutation({
    mutationFn: () => api.admin.deleteRole(role.role),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin", "roles"] });
      onDeleted();
    },
  });

  if (!role.editable) {
    return (
      <div className="rounded-lg border border-slate-200 bg-white p-4 text-sm dark:border-slate-700 dark:bg-slate-900">
        <p className="mb-2 text-slate-600 dark:text-slate-300">
          <span className="font-mono font-semibold">{role.role}</span> always holds the global wildcard and
          cannot be edited — this is a lockout guardrail so an operator can never be configured out of access.
        </p>
        <Chip label="*" />
      </div>
    );
  }

  const toggle = (permission: string, on: boolean) =>
    setDraft((prev) => {
      const next = new Set(prev);
      if (on) next.add(permission);
      else next.delete(permission);
      return next;
    });

  const setGroup = (group: PermissionGroup, on: boolean) =>
    setDraft((prev) => {
      const next = new Set(prev);
      for (const p of group.permissions) {
        if (on) next.add(p.permission);
        else next.delete(p.permission);
      }
      return next;
    });

  const extras = [...draft].filter((p) => !catalogPermissions.has(p)).sort();
  const dirty = !setsEqual(draft, original);

  const addExtra = () => {
    const value = extraInput.trim();
    if (!value) return;
    setDraft((prev) => new Set(prev).add(value));
    setExtraInput("");
  };

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500 dark:text-slate-400">
          {draft.size} permission{draft.size === 1 ? "" : "s"} granted to{" "}
          <span className="font-mono font-semibold text-slate-700 dark:text-slate-200">{role.role}</span>
        </p>
        <div className="flex items-center gap-2">
          {!role.builtIn && (
            <button
              type="button"
              disabled={del.isPending}
              onClick={() => setConfirmingDelete(true)}
              className="focus-ring rounded border border-red-300 px-3 py-1 text-xs font-medium text-red-600 hover:bg-red-50 disabled:opacity-40 dark:border-red-800 dark:hover:bg-red-900/20"
            >
              {del.isPending ? "Deleting…" : "Delete role"}
            </button>
          )}
          <button
            type="button"
            disabled={!dirty || save.isPending}
            onClick={() => setDraft(new Set(original))}
            className="focus-ring rounded border border-slate-300 px-3 py-1 text-xs font-medium text-slate-600 disabled:opacity-40 dark:border-slate-600 dark:text-slate-300"
          >
            Reset
          </button>
          <button
            type="button"
            disabled={!dirty || save.isPending}
            onClick={() => save.mutate()}
            className="focus-ring rounded bg-brand-600 px-3 py-1 text-xs font-medium text-white hover:bg-brand-500 disabled:opacity-40"
          >
            {save.isPending ? "Saving…" : "Save changes"}
          </button>
        </div>
      </div>

      {(save.isError || del.isError) && (
        <p className="text-xs text-red-600">{((save.error ?? del.error) as Error).message}</p>
      )}

      {groups.map((group) => {
        const all = group.permissions.every((p) => draft.has(p.permission));
        return (
          <section key={group.key} className="space-y-2">
            <div className="flex items-center justify-between">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-400">{group.title}</h3>
              <button
                type="button"
                onClick={() => setGroup(group, !all)}
                className="focus-ring rounded text-xs font-medium text-brand-600 hover:text-brand-500"
              >
                {all ? "Clear all" : "Select all"}
              </button>
            </div>
            <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
              {group.permissions.map((p) => (
                <label
                  key={p.permission}
                  className="flex cursor-pointer items-start gap-3 border-b border-slate-100 px-3 py-2 last:border-b-0 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50"
                >
                  <input
                    type="checkbox"
                    className="focus-ring mt-0.5"
                    checked={draft.has(p.permission)}
                    onChange={(e) => toggle(p.permission, e.target.checked)}
                  />
                  <span>
                    <span className="font-mono text-xs text-brand-700 dark:text-brand-300">{p.permission}</span>
                    <span className="block text-xs text-slate-500 dark:text-slate-400">{p.description}</span>
                  </span>
                </label>
              ))}
            </div>
          </section>
        );
      })}

      <section className="space-y-2">
        <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-400">Additional grants</h3>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Wildcards (e.g. <code className="font-mono">platform.*</code>, <code className="font-mono">tools.finance.*</code>)
          and anything not in the catalog. The escape hatch for power users.
        </p>
        <div className="flex flex-wrap items-center gap-1">
          {extras.length === 0 && <span className="text-xs text-slate-400">none</span>}
          {extras.map((p) => (
            <Chip key={p} label={p} onRemove={() => toggle(p, false)} />
          ))}
        </div>
        <form
          className="flex gap-1"
          onSubmit={(e) => {
            e.preventDefault();
            addExtra();
          }}
        >
          <input
            aria-label="Add a permission or wildcard"
            value={extraInput}
            onChange={(e) => setExtraInput(e.target.value)}
            placeholder="platform.* or tools.finance.summarize_spending"
            className="flex-1 rounded border border-slate-300 bg-white px-2 py-1 font-mono text-xs focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
          />
          <button type="submit" className="focus-ring rounded bg-slate-700 px-3 py-1 text-xs font-medium text-white hover:bg-slate-600">
            Add
          </button>
        </form>
      </section>

      <ConfirmDialog
        open={confirmingDelete}
        title="Delete role"
        body={`Delete the '${role.role}' role? It is removed from everyone who has it.`}
        confirmLabel="Delete role"
        tone="danger"
        onConfirm={() => {
          setConfirmingDelete(false);
          del.mutate();
        }}
        onCancel={() => setConfirmingDelete(false)}
      />
    </div>
  );
}

const ROLE_NAME_PATTERN = /^[a-z][a-z0-9_]{1,63}$/;

/** Create a custom role: a name plus at least one permission (free-text, with the catalog as autocomplete). */
function CreateRoleForm({
  catalogPermissions,
  onCreated,
  onCancel,
}: {
  catalogPermissions: Set<string>;
  onCreated: (role: string) => void;
  onCancel: () => void;
}) {
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [permsInput, setPermsInput] = useState("");
  const suggestions = useMemo(() => [...catalogPermissions].sort(), [catalogPermissions]);

  const permissions = permsInput.split(/[\s,]+/).map((p) => p.trim()).filter(Boolean);
  const nameValid = ROLE_NAME_PATTERN.test(name);
  const canCreate = nameValid && permissions.length > 0;

  const create = useMutation({
    mutationFn: () => api.admin.createRole(name, permissions),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["admin", "roles"] });
      onCreated(name);
    },
  });

  return (
    <form
      className="space-y-3 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"
      onSubmit={(e) => {
        e.preventDefault();
        if (canCreate) create.mutate();
      }}
    >
      <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100">New custom role</h3>
      <div>
        <input
          aria-label="Role name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="auditor"
          className="w-full rounded border border-slate-300 bg-white px-2 py-1 font-mono text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
        />
        <p className={`mt-1 text-xs ${name && !nameValid ? "text-red-600" : "text-slate-400"}`}>
          Lowercase letter, then lowercase letters, digits, or underscores (2–64 chars).
        </p>
      </div>
      <div>
        <input
          aria-label="Initial permissions"
          list="role-perm-suggestions"
          value={permsInput}
          onChange={(e) => setPermsInput(e.target.value)}
          placeholder="platform.audit.view chat.use"
          className="w-full rounded border border-slate-300 bg-white px-2 py-1 font-mono text-xs focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
        />
        <datalist id="role-perm-suggestions">
          {suggestions.map((p) => (
            <option key={p} value={p} />
          ))}
        </datalist>
        <p className="mt-1 text-xs text-slate-400">
          One or more permissions (space- or comma-separated). At least one is required; refine with the
          checkboxes after creating.
        </p>
      </div>
      {create.isError && <p className="text-xs text-red-600">{(create.error as Error).message}</p>}
      <div className="flex items-center gap-2">
        <button
          type="submit"
          disabled={!canCreate || create.isPending}
          className="focus-ring rounded bg-brand-600 px-3 py-1 text-xs font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {create.isPending ? "Creating…" : "Create role"}
        </button>
        <button
          type="button"
          onClick={onCancel}
          className="focus-ring rounded border border-slate-300 px-3 py-1 text-xs font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
        >
          Cancel
        </button>
      </div>
    </form>
  );
}

/** Configure what each role grants. Roles map to permissions; permissions are defined by the live catalog. */
export function RolesEditor() {
  const roles = useQuery({ queryKey: ["admin", "roles"], queryFn: api.admin.roles });
  const { groups, catalogPermissions, isLoading: catalogLoading, isError: catalogError } = usePermissionGroups();
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const activeRole = useMemo(
    () => roles.data?.find((r) => r.role === selected) ?? roles.data?.[0],
    [roles.data, selected],
  );

  if (roles.isLoading || catalogLoading) {
    return <p className="text-sm text-slate-500">Loading roles…</p>;
  }
  if (roles.isError || catalogError) {
    return <p className="text-sm text-red-600">Could not load the role configuration.</p>;
  }

  return (
    <div className="space-y-5">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Roles</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Configure the permissions each role grants in this tenant, or define your own custom roles. Changes
          take effect on each user's next request. The permission list is driven by the live security catalog —
          installing a module adds its tools here automatically.
        </p>
      </header>

      <div className="flex flex-wrap items-center gap-1">
        {roles.data?.map((r) => (
          <button
            key={r.role}
            type="button"
            onClick={() => {
              setCreating(false);
              setSelected(r.role);
            }}
            className={`focus-ring rounded-md px-3 py-1.5 font-mono text-xs font-medium transition-colors ${
              !creating && activeRole?.role === r.role
                ? "bg-brand-600 text-white"
                : "bg-slate-100 text-slate-600 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300"
            }`}
          >
            {r.role}
            {!r.editable && " 🔒"}
            {!r.builtIn && r.editable && <span className="ml-1 opacity-60">·custom</span>}
          </button>
        ))}
        <button
          type="button"
          onClick={() => setCreating(true)}
          className={`focus-ring rounded-md border border-dashed px-3 py-1.5 text-xs font-medium transition-colors ${
            creating
              ? "border-brand-400 bg-brand-50 text-brand-700 dark:bg-brand-900/30 dark:text-brand-200"
              : "border-slate-300 text-slate-500 hover:bg-slate-100 dark:border-slate-600 dark:hover:bg-slate-800"
          }`}
        >
          + New role
        </button>
      </div>

      {creating ? (
        <CreateRoleForm
          catalogPermissions={catalogPermissions}
          onCancel={() => setCreating(false)}
          onCreated={(role) => {
            setCreating(false);
            setSelected(role);
          }}
        />
      ) : (
        activeRole && (
          <RoleEditorPanel
            key={activeRole.role}
            role={activeRole}
            groups={groups}
            catalogPermissions={catalogPermissions}
            onDeleted={() => setSelected(null)}
          />
        )
      )}

      {/* The full permission map lives here as reference material for the editor above — what each
          permission allows, who currently grants it, and which tools are approval-gated/audited. */}
      <details className="rounded-lg border border-slate-200 dark:border-slate-700">
        <summary className="focus-ring cursor-pointer select-none rounded-lg px-4 py-3 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:text-slate-200 dark:hover:bg-slate-800/60">
          Permission reference — every permission, what it allows, and who currently grants it
        </summary>
        <div className="border-t border-slate-200 p-4 dark:border-slate-700">
          <SecurityCatalogView />
        </div>
      </details>
    </div>
  );
}
