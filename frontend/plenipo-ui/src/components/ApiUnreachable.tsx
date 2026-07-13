import { API_BASE } from "../lib/devAuth";

/**
 * The first screen a newcomer hits when the API isn't running (the most common first-run mistake).
 * Instead of a raw fetch error, it says what's wrong, where it looked, and exactly how to fix it.
 */
export function ApiUnreachable({ message, onRetry }: { message?: string; onRetry: () => void }) {
  return (
    <div role="alert" className="grid h-full place-items-center p-6">
      <div className="max-w-md text-center">
        <div className="mx-auto mb-3 flex h-10 w-10 items-center justify-center rounded-full bg-red-100 text-lg font-bold text-red-600 dark:bg-red-900/40 dark:text-red-300">
          !
        </div>
        <h2 className="text-lg font-semibold text-slate-900 dark:text-slate-100">
          Can't reach the Plenipo API
        </h2>
        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
          The dashboard couldn't load from <code className="font-mono">{API_BASE}</code>.
        </p>

        <p className="mt-4 text-sm text-slate-600 dark:text-slate-300">Make sure the backend is running:</p>
        <pre className="mt-2 overflow-x-auto rounded-md bg-slate-100 p-3 text-left text-xs text-slate-800 dark:bg-slate-800 dark:text-slate-200">
{`docker compose up -d
dotnet run --project samples/Plenipo.Sample.Host`}
        </pre>
        <p className="mt-2 text-xs text-slate-400">
          See <span className="font-medium">GETTING_STARTED.md</span>. Set{" "}
          <code className="font-mono">VITE_API_BASE</code> to point at a different API URL.
        </p>

        <button
          type="button"
          onClick={onRetry}
          className="focus-ring mt-4 rounded-md bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-500"
        >
          Retry
        </button>

        {message && (
          <p className="mt-3 break-words text-xs text-slate-400">Details: {message}</p>
        )}
      </div>
    </div>
  );
}
