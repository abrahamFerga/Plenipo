import { API_BASE } from "../lib/devAuth";
import { plenipoWebAuth } from "../lib/webAuth";

/**
 * Shown when the API is reachable but rejects the caller — a 401 (not authenticated) or 403 (missing
 * permission). Distinct from {@link ApiUnreachable} so the user isn't told to "start the backend" when the
 * backend is running fine and the real problem is authorization.
 */
export function AccessDenied({
  status,
  message,
  onRetry,
}: {
  status: number;
  message?: string;
  onRetry: () => void;
}) {
  const notSignedIn = status === 401;

  return (
    <div role="alert" className="grid h-full place-items-center p-6">
      <div className="max-w-md text-center">
        <div className="mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-full bg-amber-100 text-sm font-bold text-amber-600 dark:bg-amber-900/40 dark:text-amber-300">
          {status}
        </div>
        <h2 className="text-lg font-semibold text-slate-900 dark:text-slate-100">
          {notSignedIn ? "You're not signed in" : "You don't have access"}
        </h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          {notSignedIn ? (
            <>
              The Plenipo API at <code className="font-mono">{API_BASE}</code> rejected the request as
              unauthenticated. Sign in and try again.
            </>
          ) : (
            <>Your account doesn't have permission to view this workspace. Ask an administrator to grant access.</>
          )}
        </p>

        <div className="mt-4 flex flex-wrap gap-2">
          {/* The copy above has always told an unauthenticated visitor to "sign in and try again", while
              the UI offered only Retry — there was nothing to sign in WITH. Offer it when the configured
              adapter can actually start a sign-in; a dev-auth deployment still just retries. */}
          {notSignedIn && plenipoWebAuth().signIn && (
            <button
              type="button"
              onClick={() => void plenipoWebAuth().signIn?.()}
              className="focus-ring rounded-md bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-500"
            >
              Sign in
            </button>
          )}
          <button
            type="button"
            onClick={onRetry}
            className={
              notSignedIn && plenipoWebAuth().signIn
                ? "focus-ring rounded-md border border-slate-300 px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-600 dark:text-slate-200 dark:hover:bg-slate-800"
                : "focus-ring rounded-md bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-500"
            }
          >
            Retry
          </button>
        </div>

        {message && (
          <p className="mt-3 break-words text-xs text-slate-400">Details: {message}</p>
        )}
      </div>
    </div>
  );
}
