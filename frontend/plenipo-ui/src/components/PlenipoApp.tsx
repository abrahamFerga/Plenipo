import { useState } from "react";
import { BrowserRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AppShell } from "../routes/AppShell";
import { AppErrorBoundary } from "./AppErrorBoundary";
import type { PlenipoModuleUi } from "../lib/moduleUi";
import type { PlenipoBranding } from "../lib/branding";

export interface PlenipoAppProps {
  /**
   * Host module UI registrations (see `defineModule`). Each supplies custom React components for
   * a module's tabs; tabs without a component fall back to the server-driven generic view.
   */
  moduleUi?: readonly PlenipoModuleUi[];
  /** Product name + logo shown in the top bar. Lets a host present its own identity, not "Plenipo". */
  branding?: PlenipoBranding;
  /**
   * Bring your own React Query client to share cache/config with the rest of your app. When
   * omitted, `PlenipoApp` creates a sensible default (retry once, no refetch on window focus).
   */
  queryClient?: QueryClient;
}

/**
 * Batteries-included Plenipo frontend: wires a React Query provider and a router around the
 * platform shell so a host can mount the whole thing in one component.
 *
 * @example
 * import { PlenipoApp, defineModule } from "@plenipo/ui";
 * const finance = defineModule("finance", { tabs: { transactions: TransactionsBoard } });
 * createRoot(el).render(<PlenipoApp moduleUi={[finance]} />);
 *
 * Hosts that already own their router / query client can compose `AppShell` directly instead.
 */
export function PlenipoApp({ moduleUi, branding, queryClient }: PlenipoAppProps) {
  // Create the default client once per mount (never on re-render) so the cache is stable.
  const [client] = useState(
    () =>
      queryClient ??
      new QueryClient({
        defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
      }),
  );

  return (
    // Outermost guard: a crash in the providers, router, or shell shows a recovery screen, not a blank page.
    // (A module tab's own crash is contained closer in by TabErrorBoundary and never reaches this.)
    <AppErrorBoundary>
      <QueryClientProvider client={client}>
        {/* Opt into the React Router v7 behaviors (matches the dev harness); the shell uses absolute links. */}
        <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <AppShell moduleUi={moduleUi} branding={branding} />
        </BrowserRouter>
      </QueryClientProvider>
    </AppErrorBoundary>
  );
}
