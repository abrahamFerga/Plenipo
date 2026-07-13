import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

/**
 * Catches render/lifecycle errors thrown by the admin section it wraps, so a single broken page shows a
 * recoverable fallback instead of white-screening the entire console. Kept self-contained because the admin
 * console deliberately consumes only api/types/hooks from `@abrahamferga/cortex-ui`, not its components. The parent keys
 * this boundary on the current route, so navigating to another section mounts a fresh one and clears the error.
 */
export class AdminErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // The fallback shows the user-facing message; the component stack goes to the console for debugging.
    console.error("Admin console section failed to render:", error, info.componentStack);
  }

  render(): ReactNode {
    const { error } = this.state;
    if (!error) {
      return this.props.children;
    }

    return (
      <div
        role="alert"
        className="mx-auto mt-4 max-w-lg rounded-lg border border-red-200 bg-red-50 p-6 text-center dark:border-red-900 dark:bg-red-950/30"
      >
        <h2 className="text-base font-semibold text-red-800 dark:text-red-200">This section hit an error</h2>
        <p className="mt-2 break-words text-sm text-red-700 dark:text-red-300">{error.message}</p>
        <p className="mt-1 text-xs text-red-600/80 dark:text-red-400/80">
          Try another section from the menu, or reload the console.
        </p>
        <button
          type="button"
          onClick={() => window.location.reload()}
          className="focus-ring mt-4 rounded bg-brand-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-brand-500"
        >
          Reload console
        </button>
      </div>
    );
  }
}
