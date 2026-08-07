import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ConfirmDialog, fetchAuthConfig, type LocalUserAdmin } from "@plenipo/ui";

/**
 * Built-in sign-in (Auth:Mode=Local, ADR 0003): create accounts with a temporary password — the
 * on-prem path that works with no SMTP — and run the credential lifecycle (reset, unlock, remove
 * MFA). Renders nothing on external-IdP deployments, where credentials live at the IdP.
 * A temporary password exists in exactly ONE response: it is shown once, copyable, never stored.
 */

function TemporaryPasswordNotice({
  email,
  password,
  onDismiss,
}: {
  email: string;
  password: string;
  onDismiss: () => void;
}) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="mt-3 rounded-md border border-amber-300 bg-amber-50 p-3 dark:border-amber-700 dark:bg-amber-900/20">
      <p className="text-sm text-amber-900 dark:text-amber-200">
        Temporary password for <span className="font-medium">{email}</span> — visible only now.
        Share it securely; a change is forced at first sign-in.
      </p>
      <div className="mt-2 flex items-center gap-2">
        <code className="rounded bg-white px-2 py-1 font-mono text-sm dark:bg-slate-900">{password}</code>
        <button
          type="button"
          onClick={() => {
            void navigator.clipboard.writeText(password).then(() => setCopied(true));
          }}
          className="focus-ring rounded border border-amber-400 px-2 py-1 text-xs font-medium text-amber-800 hover:bg-amber-100 dark:text-amber-200 dark:hover:bg-amber-900/40"
        >
          {copied ? "Copied" : "Copy"}
        </button>
        <button
          type="button"
          onClick={onDismiss}
          className="focus-ring rounded px-2 py-1 text-xs text-amber-700 hover:underline dark:text-amber-300"
        >
          Dismiss
        </button>
      </div>
    </div>
  );
}

function LocalUserRow({
  user,
  onReset,
  onUnlock,
  onResetTotp,
}: {
  user: LocalUserAdmin;
  onReset: () => void;
  onUnlock: () => void;
  onResetTotp: () => void;
}) {
  const locked = user.lockedUntil != null && new Date(user.lockedUntil) > new Date();
  return (
    <li className="flex flex-wrap items-center justify-between gap-2 py-1.5 text-sm">
      <span className="min-w-0 truncate text-slate-700 dark:text-slate-300">
        {user.displayName ?? user.email}
        <span className="ml-2 text-xs text-slate-400">{user.email}</span>
        {locked && (
          <span className="ml-2 rounded-full bg-red-100 px-2 py-0.5 text-xs text-red-700 dark:bg-red-900/40 dark:text-red-200">
            locked
          </span>
        )}
        {user.mustChangePassword && (
          <span className="ml-2 rounded-full bg-amber-100 px-2 py-0.5 text-xs text-amber-800 dark:bg-amber-900/40 dark:text-amber-200">
            must change password
          </span>
        )}
        {user.totpEnabled && (
          <span className="ml-2 rounded-full bg-emerald-100 px-2 py-0.5 text-xs text-emerald-700 dark:bg-emerald-900/40 dark:text-emerald-200">
            MFA
          </span>
        )}
      </span>
      <span className="flex shrink-0 items-center gap-1">
        <button
          type="button"
          onClick={onReset}
          className="focus-ring rounded px-2 py-0.5 text-xs font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          Reset password
        </button>
        {locked && (
          <button
            type="button"
            onClick={onUnlock}
            className="focus-ring rounded px-2 py-0.5 text-xs font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
          >
            Unlock
          </button>
        )}
        {user.totpEnabled && (
          <button
            type="button"
            onClick={onResetTotp}
            className="focus-ring rounded px-2 py-0.5 text-xs font-medium text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30"
          >
            Remove MFA
          </button>
        )}
      </span>
    </li>
  );
}

export function LocalUsersPanel({ allRoles }: { allRoles: string[] }) {
  const qc = useQueryClient();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roles, setRoles] = useState<string[]>([]);
  const [reveal, setReveal] = useState<{ email: string; password: string } | null>(null);
  const [confirming, setConfirming] = useState<{ kind: "reset" | "totp"; user: LocalUserAdmin } | null>(null);

  // The panel keys on the deployment's shape, not on a permission: external-IdP deployments manage
  // credentials at the IdP, and rendering dead controls there would be a lie.
  const authConfig = useQuery({ queryKey: ["platform", "auth-config"], queryFn: () => fetchAuthConfig() });
  const enabled = authConfig.data?.local === true;

  const users = useQuery({ queryKey: ["admin", "local-users"], queryFn: api.admin.localUsers, enabled });
  const invalidate = () => {
    void qc.invalidateQueries({ queryKey: ["admin", "local-users"] });
    void qc.invalidateQueries({ queryKey: ["admin", "users"] });
  };

  const create = useMutation({
    mutationFn: () => api.admin.createLocalUser(email.trim(), displayName.trim() || null, roles),
    onSuccess: (r) => {
      setReveal({ email: r.email, password: r.temporaryPassword });
      setEmail("");
      setDisplayName("");
      setRoles([]);
      invalidate();
    },
  });
  const reset = useMutation({
    mutationFn: (user: LocalUserAdmin) => api.admin.resetLocalPassword(user.userId),
    onSuccess: (r, user) => {
      setReveal({ email: user.email, password: r.temporaryPassword });
      invalidate();
    },
  });
  const unlock = useMutation({
    mutationFn: (user: LocalUserAdmin) => api.admin.unlockLocalUser(user.userId),
    onSuccess: invalidate,
  });
  const resetTotp = useMutation({
    mutationFn: (user: LocalUserAdmin) => api.admin.resetLocalTotp(user.userId),
    onSuccess: invalidate,
  });

  if (!enabled) {
    return null;
  }

  const failure = [create, reset, unlock, resetTotp].find((m) => m.isError);

  return (
    <section className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <h2 className="text-sm font-semibold text-slate-900 dark:text-slate-100">Local sign-in accounts</h2>
      <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
        This deployment signs users in itself — create an account with a temporary password and hand
        it over; no email server needed.
      </p>

      <form
        className="mt-3 flex flex-wrap items-end gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          setReveal(null);
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
        <label className="min-w-44">
          <span className="text-xs font-medium text-slate-600 dark:text-slate-300">Name (optional)</span>
          <input
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            placeholder="Ada Lovelace"
            className="focus-ring mt-1 w-full rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
          />
        </label>
        <fieldset className="flex flex-wrap items-center gap-2">
          <legend className="text-xs font-medium text-slate-600 dark:text-slate-300">Roles</legend>
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
          {create.isPending ? "Creating…" : "Create account"}
        </button>
      </form>

      {failure && <p className="mt-2 text-sm text-red-600">{(failure.error as Error).message}</p>}
      {reveal && (
        <TemporaryPasswordNotice
          email={reveal.email}
          password={reveal.password}
          onDismiss={() => setReveal(null)}
        />
      )}

      {(users.data?.length ?? 0) > 0 && (
        <ul className="mt-4 divide-y divide-slate-100 border-t border-slate-100 pt-2 dark:divide-slate-800 dark:border-slate-800">
          {users.data!.map((u) => (
            <LocalUserRow
              key={u.userId}
              user={u}
              onReset={() => setConfirming({ kind: "reset", user: u })}
              onUnlock={() => unlock.mutate(u)}
              onResetTotp={() => setConfirming({ kind: "totp", user: u })}
            />
          ))}
        </ul>
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={confirming?.kind === "totp" ? "Remove two-factor" : "Reset password"}
        body={
          confirming?.kind === "totp"
            ? `Remove the authenticator app from ${confirming.user.email}? They can re-enroll after signing in.`
            : `Reset ${confirming?.user.email}'s password? Their sessions end as tokens refresh, and a new temporary password is shown once.`
        }
        confirmLabel={confirming?.kind === "totp" ? "Remove MFA" : "Reset"}
        tone="danger"
        onConfirm={() => {
          if (confirming?.kind === "reset") reset.mutate(confirming.user);
          if (confirming?.kind === "totp") resetTotp.mutate(confirming.user);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
      />
    </section>
  );
}
