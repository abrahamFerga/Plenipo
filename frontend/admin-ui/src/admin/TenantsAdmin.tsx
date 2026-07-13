import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ConfirmDialog, type AdminTenant } from "@plenipo/ui";

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString();
}

function TenantRow({ tenant }: { tenant: AdminTenant }) {
  const qc = useQueryClient();
  const [confirmingDeactivate, setConfirmingDeactivate] = useState(false);
  const setActive = useMutation({
    mutationFn: (isActive: boolean) => api.admin.setTenantActive(tenant.id, isActive),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "tenants"] }),
  });

  return (
    <div
      className={`flex items-center justify-between gap-4 rounded-lg border bg-white p-4 dark:bg-slate-900 ${
        tenant.isActive ? "border-slate-200 dark:border-slate-700" : "border-red-200 dark:border-red-900/60"
      }`}
    >
      <div className="min-w-0">
        <p className="flex items-center gap-2 font-medium text-slate-900 dark:text-slate-100">
          {tenant.name}
          <span className="font-mono text-xs text-slate-400">{tenant.slug}</span>
          {!tenant.isActive && (
            <span className="rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700 dark:bg-red-900/40 dark:text-red-200">
              inactive
            </span>
          )}
        </p>
        <p className="mt-0.5 text-xs text-slate-400">created {formatDate(tenant.createdAt)}</p>
      </div>
      <button
        type="button"
        disabled={setActive.isPending}
        onClick={() => (tenant.isActive ? setConfirmingDeactivate(true) : setActive.mutate(true))}
        className={`focus-ring rounded border px-2 py-1 text-xs font-medium disabled:opacity-40 ${
          tenant.isActive
            ? "border-red-300 text-red-600 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20"
            : "border-emerald-300 text-emerald-700 hover:bg-emerald-50 dark:border-emerald-800 dark:hover:bg-emerald-900/20"
        }`}
      >
        {setActive.isPending ? "…" : tenant.isActive ? "Deactivate" : "Activate"}
      </button>

      <ConfirmDialog
        open={confirmingDeactivate}
        title="Deactivate tenant"
        body={`Deactivate ${tenant.name}? All of this tenant's users will be denied access.`}
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

/**
 * Cross-tenant operator view: every tenant in the deployment, with an activate/deactivate switch. A
 * deactivated tenant denies all of its users (a tenant-wide kill switch). Requires platform.tenants.manage —
 * the API returns 403 otherwise, so this surface is meaningful only to platform operators.
 */
function CreateTenantForm() {
  const qc = useQueryClient();
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const create = useMutation({
    mutationFn: () => api.admin.createTenant(name.trim(), slug.trim()),
    onSuccess: () => {
      setName("");
      setSlug("");
      void qc.invalidateQueries({ queryKey: ["admin", "tenants"] });
    },
  });
  const slugInvalid = slug.trim() !== "" && !/^[a-z0-9][a-z0-9-]{0,62}$/.test(slug.trim());

  return (
    <form
      className="flex flex-wrap items-end gap-3 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900"
      onSubmit={(e) => {
        e.preventDefault();
        if (!slugInvalid) create.mutate();
      }}
    >
      <div className="space-y-1">
        <label htmlFor="tenant-name" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
          Name
        </label>
        <input
          id="tenant-name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Acme Legal LLP"
          className="w-56 rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
        />
      </div>
      <div className="space-y-1">
        <label htmlFor="tenant-slug" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
          Slug
        </label>
        <input
          id="tenant-slug"
          value={slug}
          onChange={(e) => setSlug(e.target.value.toLowerCase())}
          placeholder="acme-legal"
          className="w-44 rounded border border-slate-300 bg-white px-2 py-1 font-mono text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800"
        />
      </div>
      <button
        type="submit"
        disabled={create.isPending || !name.trim() || !slug.trim() || slugInvalid}
        className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
      >
        {create.isPending ? "Creating…" : "Create tenant"}
      </button>
      <div className="basis-full text-xs">
        {slugInvalid && <p className="text-red-600">Slug: lowercase letters, digits, and hyphens only.</p>}
        {create.isError && <p className="text-red-600">{(create.error as Error).message}</p>}
        {!slugInvalid && !create.isError && (
          <p className="text-slate-400">
            The slug is the tenant's stable identity — sign-in mapping, channels, and dev auth reference it.
          </p>
        )}
      </div>
    </form>
  );
}

export function TenantsAdmin() {
  const tenants = useQuery({ queryKey: ["admin", "tenants"], queryFn: api.admin.tenants });

  if (tenants.isLoading) {
    return <p className="text-sm text-slate-500">Loading tenants…</p>;
  }
  if (tenants.isError) {
    return (
      <p className="text-sm text-red-600">
        Could not load tenants — this view requires the platform-operator permission.
      </p>
    );
  }

  const rows = tenants.data ?? [];

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Tenants</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Every tenant in this deployment. Deactivating a tenant denies all of its users until reactivated;
          changes are recorded in the audit trail.
        </p>
      </header>

      <CreateTenantForm />

      {rows.length === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
          No tenants found.
        </p>
      ) : (
        <div className="space-y-2">
          {rows.map((t) => (
            <TenantRow key={t.id} tenant={t} />
          ))}
        </div>
      )}
    </div>
  );
}
