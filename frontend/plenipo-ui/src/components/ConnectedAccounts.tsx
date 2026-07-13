import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { api, type UserConnector } from "../lib/api";

/**
 * The end-user "connected accounts" page: which delegated data sources this tenant offers, and
 * whether YOUR account is linked. Connecting opens the provider's consent page in a new tab
 * (the OAuth callback lands server-side); Disconnect removes only your stored tokens. Tenant
 * enablement itself is an admin concern — this page only ever manages the caller's own logins.
 */
export function ConnectedAccounts() {
  const connectors = useQuery({ queryKey: ["user-connectors"], queryFn: api.connectors.list });

  if (connectors.isPending) {
    return <p className="text-sm text-slate-500 dark:text-slate-400">Loading connected accounts…</p>;
  }
  if (connectors.isError) {
    return (
      <p className="text-sm text-red-600 dark:text-red-400">Could not load your connected accounts.</p>
    );
  }

  return (
    <div className="max-w-2xl">
      <h2 className="mb-1 text-lg font-semibold text-slate-900 dark:text-slate-100">
        Connected accounts
      </h2>
      <p className="mb-4 text-sm text-slate-500 dark:text-slate-400">
        Link your own accounts so the assistant can reach your documents. Access uses your
        permissions at the provider — nobody else&apos;s.
      </p>

      {connectors.data.length === 0 ? (
        <p className="rounded-lg border border-dashed border-slate-300 p-6 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
          No connectable data sources are enabled for your organization.
        </p>
      ) : (
        <ul className="space-y-3">
          {connectors.data.map((c) => (
            <ConnectorRow key={c.id} connector={c} />
          ))}
        </ul>
      )}

      {/* The account area's other page: the ADMT disclosure of what the AI did or proposed. The
          user's name in the top bar opens THIS page, so this is where the sibling gets its door. */}
      <p className="mt-6 border-t border-slate-200 pt-4 text-sm dark:border-slate-700">
        <Link
          to="/account/ai-decisions"
          className="focus-ring rounded font-medium text-brand-600 hover:underline dark:text-brand-400"
        >
          AI decision history
        </Link>
        <span className="ml-2 text-slate-500 dark:text-slate-400">
          — what the assistant did or proposed, and who approved it.
        </span>
      </p>
    </div>
  );
}

function ConnectorRow({ connector }: { connector: UserConnector }) {
  const qc = useQueryClient();

  const connect = useMutation({
    mutationFn: () => api.connectors.oauthStart(connector.id),
    onSuccess: ({ authorizeUrl }) => {
      // The consent page finishes on the server's callback; the list refreshes on refocus.
      window.open(authorizeUrl, "_blank", "noopener");
    },
  });

  const disconnect = useMutation({
    mutationFn: () => api.connectors.disconnect(connector.id),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["user-connectors"] }),
  });

  const busy = connect.isPending || disconnect.isPending;
  return (
    <li className="flex items-center justify-between gap-4 rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <div className="min-w-0">
        <p className="text-sm font-semibold text-slate-900 dark:text-slate-100">
          {connector.displayName}
          {connector.connected && (
            <span className="ml-2 rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-900/40 dark:text-green-300">
              Connected
            </span>
          )}
        </p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">
          {connector.description}
        </p>
        {(connect.isError || disconnect.isError) && (
          <p className="mt-1 text-xs text-red-600">
            {((connect.error ?? disconnect.error) as Error).message}
          </p>
        )}
      </div>
      {connector.connected ? (
        <button
          type="button"
          disabled={busy}
          onClick={() => disconnect.mutate()}
          className="focus-ring shrink-0 rounded border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-40 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          {disconnect.isPending ? "Disconnecting…" : "Disconnect"}
        </button>
      ) : (
        <button
          type="button"
          disabled={busy}
          onClick={() => connect.mutate()}
          className="focus-ring shrink-0 rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {connect.isPending ? "Opening…" : "Connect"}
        </button>
      )}
    </li>
  );
}
