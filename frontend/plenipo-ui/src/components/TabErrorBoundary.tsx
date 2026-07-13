import { Component, type ErrorInfo, type ReactNode } from "react";

interface TabErrorBoundaryProps {
  children: ReactNode;
  /** Shown in the fallback so the user knows which view failed. */
  label?: string;
}

interface TabErrorBoundaryState {
  error: Error | null;
}

/**
 * Catches render errors thrown by a module tab's content — including host-provided components — so a
 * single buggy tab shows a contained error card instead of white-screening the whole shell (React
 * unmounts the entire tree on an uncaught render error). Navigating to another tab remounts this
 * boundary, clearing the error; "Try again" re-attempts the same view in place.
 */
export class TabErrorBoundary extends Component<
  TabErrorBoundaryProps,
  TabErrorBoundaryState
> {
  state: TabErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): TabErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // The shell keeps running; surface the failure (with component stack) for whoever's debugging.
    console.error("Plenipo: a module tab failed to render", error, info.componentStack);
  }

  private reset = (): void => this.setState({ error: null });

  render(): ReactNode {
    const { error } = this.state;
    if (!error) {
      return this.props.children;
    }

    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-6 text-sm dark:border-red-900/50 dark:bg-red-950/30">
        <h2 className="mb-1 font-semibold text-red-800 dark:text-red-200">
          This view failed to load{this.props.label ? `: ${this.props.label}` : ""}
        </h2>
        <p className="mb-3 break-words font-mono text-xs text-red-700 dark:text-red-300">
          {error.message}
        </p>
        <button
          type="button"
          onClick={this.reset}
          className="focus-ring rounded-md border border-red-300 px-3 py-1 text-xs font-medium text-red-700 hover:bg-red-100 dark:border-red-800 dark:text-red-200 dark:hover:bg-red-900/40"
        >
          Try again
        </button>
      </div>
    );
  }
}
