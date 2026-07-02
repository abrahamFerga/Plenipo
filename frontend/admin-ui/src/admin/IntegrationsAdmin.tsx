import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, type ConnectorAdmin } from "@cortex/ui";

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

/**
 * The schema-driven settings form: fields come from the connector's manifest. Secrets render as
 * password inputs and are write-only — a stored secret shows only "value is set"; leaving the
 * field blank keeps it, typing replaces it.
 */
function SettingsForm({ connector }: { connector: ConnectorAdmin }) {
  const qc = useQueryClient();
  const [values, setValues] = useState<Record<string, string>>({});

  const save = useMutation({
    mutationFn: () => api.admin.setConnectorSettings(connector.id, values),
    onSuccess: () => {
      setValues({});
      void qc.invalidateQueries({ queryKey: ["admin", "connectors"] });
    },
  });

  const dirty = Object.keys(values).length > 0;

  return (
    <form
      className="mt-3 space-y-3 border-t border-slate-100 pt-3 dark:border-slate-800"
      onSubmit={(e) => {
        e.preventDefault();
        if (dirty) save.mutate();
      }}
    >
      {connector.settings.map((s) => (
        <label key={s.key} className="block">
          <span className="text-sm font-medium text-slate-700 dark:text-slate-300">
            {s.label}
            {s.required && <span className="text-red-500"> *</span>}
            {s.isSecret && s.hasValue && (
              <span className="ml-2 rounded-full bg-emerald-50 px-2 py-0.5 text-xs text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-300">
                value is set
              </span>
            )}
          </span>
          <input
            type={s.isSecret ? "password" : "text"}
            value={values[s.key] ?? ""}
            placeholder={
              s.isSecret
                ? s.hasValue
                  ? "•••••••• (leave blank to keep)"
                  : ""
                : s.hasValue
                  ? "(value is set — type to replace)"
                  : ""
            }
            onChange={(e) => setValues((v) => ({ ...v, [s.key]: e.target.value }))}
            className="focus-ring mt-1 w-full rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
          />
          {s.description && (
            <span className="mt-0.5 block text-xs text-slate-400">{s.description}</span>
          )}
        </label>
      ))}

      <div className="flex items-center gap-3">
        <button
          type="submit"
          disabled={!dirty || save.isPending}
          className="focus-ring rounded-md bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-700 disabled:opacity-50"
        >
          {save.isPending ? "Saving…" : "Save settings"}
        </button>
        {save.isError && <span className="text-sm text-red-600">{(save.error as Error).message}</span>}
        {save.isSuccess && !dirty && <span className="text-sm text-emerald-600">Saved.</span>}
      </div>
    </form>
  );
}

function ConnectorCard({ connector }: { connector: ConnectorAdmin }) {
  const qc = useQueryClient();
  const [expanded, setExpanded] = useState(false);
  const toggle = useMutation({
    mutationFn: (enabled: boolean) => api.admin.setConnectorEnabled(connector.id, enabled),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["admin", "connectors"] }),
  });
  // Stage 2 for delegated connectors: link YOUR account (each user does this individually).
  const connect = useMutation({
    mutationFn: () => api.connectors.oauthStart(connector.id),
    onSuccess: ({ authorizeUrl }) => window.open(authorizeUrl, "_blank", "noopener"),
  });

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <div className="flex items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="flex items-center gap-2 font-medium text-slate-900 dark:text-slate-100">
            {connector.displayName}
            <span className="font-mono text-xs text-slate-400">{connector.id}</span>
            {!connector.enabled && (
              <span className="rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-500 dark:bg-slate-800">
                disabled
              </span>
            )}
          </p>
          <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">{connector.description}</p>
          <p className="mt-1 text-xs text-slate-400">
            {connector.tools.length} tool(s) · {connector.authMode} auth
            {connector.supportsSync ? " · sync-capable" : ""}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-3">
          {connector.authMode === "UserDelegated" && connector.enabled && (
            <button
              type="button"
              onClick={() => connect.mutate()}
              disabled={connect.isPending}
              title="Link your own account (opens the identity provider's consent page)"
              className="focus-ring rounded-md border border-brand-600 px-2 py-1 text-sm font-medium text-brand-600 hover:bg-brand-50 disabled:opacity-50 dark:hover:bg-slate-800"
            >
              {connect.isPending ? "Opening…" : "Connect account"}
            </button>
          )}
          {connector.settings.length > 0 && (
            <button
              type="button"
              onClick={() => setExpanded((v) => !v)}
              aria-expanded={expanded}
              className="focus-ring rounded-md px-2 py-1 text-sm font-medium text-slate-500 hover:text-slate-900 dark:hover:text-slate-100"
            >
              {expanded ? "Hide settings" : "Settings"}
            </button>
          )}
          <Toggle on={connector.enabled} disabled={toggle.isPending} onChange={(next) => toggle.mutate(next)} />
        </div>
      </div>

      {expanded && <SettingsForm connector={connector} />}
    </div>
  );
}

/**
 * Per-tenant data-source connectors (the Integrations page). Connectors ship with the deployment
 * but are default-off: enabling one here is what makes its tools exist for this tenant's agents —
 * each tool still individually permission-gated, fetches approval-gated, everything audited.
 */
export function IntegrationsAdmin() {
  const connectors = useQuery({ queryKey: ["admin", "connectors"], queryFn: api.admin.connectors });

  if (connectors.isLoading) {
    return <p className="text-sm text-slate-500">Loading integrations…</p>;
  }
  if (connectors.isError) {
    return <p className="text-sm text-red-600">{(connectors.error as Error).message}</p>;
  }

  const rows = connectors.data ?? [];

  return (
    <div className="space-y-4">
      <header>
        <h1 className="text-xl font-semibold text-slate-900 dark:text-slate-100">Integrations</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Connect this tenant to where its data already lives. Connectors are off until enabled here;
          secret settings are write-only and stored protected. Changes land in the audit trail.
        </p>
      </header>

      {rows.length === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-400 dark:border-slate-700">
          No connectors are installed in this deployment.
        </p>
      ) : (
        <div className="space-y-2">
          {rows.map((c) => (
            <ConnectorCard key={c.id} connector={c} />
          ))}
        </div>
      )}
    </div>
  );
}
