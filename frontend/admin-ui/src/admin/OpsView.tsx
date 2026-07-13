import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@plenipo/ui";

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <h3 className="mb-3 text-sm font-semibold text-slate-900 dark:text-slate-100">{title}</h3>
      {children}
    </section>
  );
}

function Stat({ label, value, alarm = false }: { label: string; value: string; alarm?: boolean }) {
  return (
    <div>
      <dt className="text-xs text-slate-500 dark:text-slate-400">{label}</dt>
      <dd
        className={`text-lg font-semibold ${
          alarm ? "text-red-600 dark:text-red-400" : "text-slate-900 dark:text-slate-100"
        }`}
      >
        {value}
      </dd>
    </div>
  );
}

const inputClass =
  "w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800";

/**
 * Webhook delivery settings (GET/PUT /api/admin/notification-settings). The signing secret is
 * write-only: the server only ever reports that one is on file — null keeps it, "" clears it.
 * Requires platform.notifications.manage; the read fails harmlessly for operators without it.
 */
function NotificationDeliveryCard() {
  const qc = useQueryClient();
  const settings = useQuery({
    queryKey: ["admin", "notification-settings"],
    queryFn: api.admin.notificationSettings,
    retry: false,
  });
  const [url, setUrl] = useState("");
  const [secret, setSecret] = useState("");
  const [clearSecret, setClearSecret] = useState(false);

  useEffect(() => {
    if (settings.data) {
      setUrl(settings.data.webhookUrl ?? "");
      setSecret("");
      setClearSecret(false);
    }
  }, [settings.data]);

  const save = useMutation({
    mutationFn: () =>
      api.admin.setNotificationSettings({
        webhookUrl: url.trim() || null,
        // Write-only contract: null keeps the stored secret, "" clears it, a value replaces it.
        webhookSecret: clearSecret ? "" : secret.trim() || null,
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["admin", "notification-settings"] });
      void qc.invalidateQueries({ queryKey: ["admin", "ops"] });
    },
  });

  if (settings.isError) {
    return (
      <Card title="Notification delivery">
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Requires the platform.notifications.manage permission.
        </p>
      </Card>
    );
  }

  // Don't offer the form until the stored values arrive — the prefill effect above would
  // otherwise overwrite anything typed in the meantime.
  if (settings.isPending) {
    return (
      <Card title="Notification delivery">
        <p className="text-sm text-slate-500 dark:text-slate-400">Loading…</p>
      </Card>
    );
  }

  const urlInvalid = url.trim() !== "" && !/^https?:\/\/\S+$/.test(url.trim());
  return (
    <Card title="Notification delivery">
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault();
          if (!urlInvalid) save.mutate();
        }}
      >
        <div className="space-y-1">
          <label htmlFor="webhook-url" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            Webhook URL
          </label>
          <input
            id="webhook-url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="https://example.com/hooks/plenipo — blank disables delivery"
            className={inputClass}
          />
          {urlInvalid && <p className="text-xs text-red-600">Enter an absolute http(s) URL, or leave blank.</p>}
        </div>
        <div className="space-y-1">
          <label htmlFor="webhook-secret" className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            Signing secret
          </label>
          <input
            id="webhook-secret"
            type="password"
            autoComplete="off"
            value={secret}
            onChange={(e) => {
              setSecret(e.target.value);
              setClearSecret(false);
            }}
            placeholder={
              settings.data?.hasWebhookSecret
                ? "A secret is on file — enter a new one to replace it"
                : "Used to sign deliveries (X-Plenipo-Signature)"
            }
            className={inputClass}
          />
          <p className="text-xs text-slate-400">
            Stored write-only in the secret vault — never shown again, only replaced or cleared.
          </p>
          {settings.data?.hasWebhookSecret && (
            <label className="flex items-center gap-1.5 text-xs text-slate-500 dark:text-slate-400">
              <input
                type="checkbox"
                checked={clearSecret}
                onChange={(e) => {
                  setClearSecret(e.target.checked);
                  setSecret("");
                }}
              />
              Clear the stored secret
            </label>
          )}
        </div>
        {save.isError && <p className="text-xs text-red-600">{(save.error as Error).message}</p>}
        <button
          type="submit"
          disabled={urlInvalid || settings.isPending || save.isPending}
          className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {save.isPending ? "Saving…" : "Save"}
        </button>
      </form>
    </Card>
  );
}

function ago(iso?: string): string {
  if (!iso) return "never";
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/**
 * Operational health at a glance — the server aggregates everything into one tenant-scoped call
 * (GET /api/admin/ops): job queue, connector sync recency, knowledge-index freshness, delivery
 * config, and budget posture. Requires platform.audit.view.
 */
export function OpsView() {
  const ops = useQuery({
    queryKey: ["admin", "ops"],
    queryFn: api.admin.ops,
    refetchInterval: 15_000,
  });

  if (ops.isPending) {
    return <p className="text-sm text-slate-500 dark:text-slate-400">Loading operational snapshot…</p>;
  }

  if (ops.isError || !ops.data) {
    return <p className="text-sm text-red-600 dark:text-red-400">Could not load the ops snapshot.</p>;
  }

  const d = ops.data;
  const budgetUsedPct =
    d.ai.maxMonthlyTokens > 0 ? Math.min(100, Math.round((d.ai.monthTokens / d.ai.maxMonthlyTokens) * 100)) : null;

  return (
    <div className="max-w-4xl">
      <h2 className="mb-1 text-lg font-semibold text-slate-900 dark:text-slate-100">Operations</h2>
      <p className="mb-4 text-sm text-slate-500 dark:text-slate-400">
        Live tenant health — refreshes every 15 seconds.
      </p>

      <div className="grid gap-4 sm:grid-cols-2">
        <Card title="Background jobs">
          <dl className="grid grid-cols-3 gap-3">
            <Stat label="Queued" value={String(d.jobs.queued)} />
            <Stat label="Running" value={String(d.jobs.running)} />
            <Stat label="Failed (24h)" value={String(d.jobs.failed24h)} alarm={d.jobs.failed24h > 0} />
          </dl>
          {d.jobs.oldestQueuedAgeSeconds != null && d.jobs.oldestQueuedAgeSeconds > 300 && (
            <p className="mt-2 text-xs text-amber-600 dark:text-amber-400">
              Oldest queued job has waited {Math.floor(d.jobs.oldestQueuedAgeSeconds / 60)} minutes — the
              processor may be behind.
            </p>
          )}
        </Card>

        <Card title="Knowledge index">
          <dl className="grid grid-cols-3 gap-3">
            <Stat label="Collections" value={String(d.rag.collections)} />
            <Stat label="Chunks" value={String(d.rag.chunks)} />
            <Stat label="Last ingest" value={ago(d.rag.lastIngestAt)} />
          </dl>
        </Card>

        <Card title="Connectors">
          {d.connectors.length === 0 ? (
            <p className="text-sm text-slate-500 dark:text-slate-400">No connectors enabled.</p>
          ) : (
            <ul className="space-y-1.5 text-sm">
              {d.connectors.map((c) => (
                <li key={c.connectorId} className="flex items-center justify-between">
                  <span className="font-medium text-slate-900 dark:text-slate-100">{c.connectorId}</span>
                  <span className="text-xs text-slate-500 dark:text-slate-400">
                    {c.bindingCount} binding{c.bindingCount === 1 ? "" : "s"} · synced {ago(c.lastSyncedAt)}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>

        <Card title="AI & budget">
          <dl className="grid grid-cols-2 gap-3">
            <Stat label="Provider" value={`${d.ai.provider} / ${d.ai.model}`} />
            <Stat
              label="Month tokens"
              value={
                budgetUsedPct == null
                  ? d.ai.monthTokens.toLocaleString()
                  : `${d.ai.monthTokens.toLocaleString()} (${budgetUsedPct}%)`
              }
              alarm={budgetUsedPct != null && budgetUsedPct >= 80}
            />
          </dl>
          <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">
            {d.ai.maxMonthlyTokens > 0
              ? `Monthly budget: ${d.ai.maxMonthlyTokens.toLocaleString()} tokens.`
              : "No monthly budget set (unlimited)."}
          </p>
        </Card>

        {/* Webhook config is editable right where its health is reported. */}
        <NotificationDeliveryCard />
      </div>
    </div>
  );
}
