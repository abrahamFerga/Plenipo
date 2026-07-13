import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@abrahamferga/cortex-ui";

/**
 * Standing email invites: name an address and starting roles BEFORE the person has ever signed
 * in; their roles apply automatically at first sign-in with that email. When SMTP isn't
 * configured the invite still works — the response says to share the sign-in link manually.
 */
export function InvitesPanel({ allRoles }: { allRoles: string[] }) {
  const qc = useQueryClient();
  const [email, setEmail] = useState("");
  const [roles, setRoles] = useState<string[]>([]);
  const [result, setResult] = useState<string | null>(null);

  const invites = useQuery({ queryKey: ["admin", "invites"], queryFn: api.admin.invites });
  const invalidate = () => qc.invalidateQueries({ queryKey: ["admin", "invites"] });

  const create = useMutation({
    mutationFn: () => api.admin.createInvite(email.trim(), roles),
    onSuccess: (r) => {
      setResult(r.message);
      setEmail("");
      setRoles([]);
      invalidate();
    },
  });
  const revoke = useMutation({ mutationFn: api.admin.revokeInvite, onSuccess: invalidate });

  const pending = (invites.data ?? []).filter((i) => !i.redeemedAt);

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <h2 className="text-sm font-semibold text-slate-900 dark:text-slate-100">Invite someone</h2>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
        Their roles apply the first time they sign in with this email — no account needed yet.
      </p>

      <form
        className="mt-3 flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          setResult(null);
          if (email.trim()) create.mutate();
        }}
      >
        <label className="min-w-56 flex-1">
          <span className="text-xs font-medium text-slate-600 dark:text-slate-300">Email</span>
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="ada@example.com"
            className="focus-ring mt-1 w-full rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
          />
        </label>
        <fieldset className="flex flex-wrap items-center gap-2">
          <legend className="text-xs font-medium text-slate-600 dark:text-slate-300">Starting roles</legend>
          {allRoles.map((r) => (
            <label key={r} className="inline-flex items-center gap-1 text-xs text-slate-700 dark:text-slate-300">
              <input
                type="checkbox"
                checked={roles.includes(r)}
                onChange={(e) =>
                  setRoles((prev) => (e.target.checked ? [...prev, r] : prev.filter((x) => x !== r)))
                }
              />
              <span className="font-mono">{r}</span>
            </label>
          ))}
        </fieldset>
        <button
          type="submit"
          disabled={!email.trim() || create.isPending}
          className="focus-ring rounded-md bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-700 disabled:opacity-50"
        >
          {create.isPending ? "Inviting…" : "Invite"}
        </button>
      </form>

      {create.isError && <p className="mt-2 text-sm text-red-600">{(create.error as Error).message}</p>}
      {result && <p className="mt-2 text-sm text-emerald-700 dark:text-emerald-400">{result}</p>}

      {pending.length > 0 && (
        <ul className="mt-4 space-y-1 border-t border-slate-100 pt-3 dark:border-slate-800">
          {pending.map((i) => (
            <li key={i.id} className="flex items-center justify-between gap-2 text-sm">
              <span className="min-w-0 truncate text-slate-700 dark:text-slate-300">
                {i.email}
                {i.roles.length > 0 && (
                  <span className="ml-2 font-mono text-xs text-slate-400">{i.roles.join(", ")}</span>
                )}
              </span>
              <button
                type="button"
                onClick={() => revoke.mutate(i.id)}
                className="focus-ring shrink-0 rounded px-2 py-0.5 text-xs font-medium text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
              >
                Revoke
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
