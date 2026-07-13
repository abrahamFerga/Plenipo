import { Component, type ErrorInfo, type ReactNode } from "react";

interface AppErrorBoundaryProps {
  children: ReactNode;
}

interface AppErrorBoundaryState {
  error: Error | null;
}

/**
 * The outermost render-error guard for the Plenipo frontend. {@link TabErrorBoundary} contains a crash within a
 * single module tab; this catches everything else — the shell chrome, routing, the query/branding providers —
 * so a failure there shows a full-screen recovery card instead of a blank page (React unmounts the whole tree
 * on an uncaught render error). A hard shell failure can't be retried in place, so the action is a full reload.
 */
export class AppErrorBoundary extends Component<
  AppErrorBoundaryProps,
  AppErrorBoundaryState
> {
  state: AppErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): AppErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Nothing above this remains mounted; surface the failure (with component stack) for debugging.
    console.error("Plenipo: the application shell failed to render", error, info.componentStack);
  }

  render(): ReactNode {
    const { error } = this.state;
    if (!error) {
      return this.props.children;
    }

    return (
      <div
        role="alert"
        className="grid min-h-screen place-items-center bg-slate-50 p-6 dark:bg-slate-950"
      >
        <div className="max-w-md rounded-lg border border-red-200 bg-red-50 p-6 text-center dark:border-red-900/50 dark:bg-red-950/30">
          <h1 className="text-base font-semibold text-red-800 dark:text-red-200">Something went wrong</h1>
          <p className="mt-2 break-words font-mono text-xs text-red-700 dark:text-red-300">{error.message}</p>
          <p className="mt-2 text-xs text-red-600/80 dark:text-red-400/80">
            The application ran into an unexpected error. Reloading usually fixes it.
          </p>
          <button
            type="button"
            onClick={() => window.location.reload()}
            className="focus-ring mt-4 rounded-md bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500"
          >
            Reload
          </button>
        </div>
      </div>
    );
  }
}
