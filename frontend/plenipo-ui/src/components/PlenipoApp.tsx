import { useEffect, useState } from "react";
import { BrowserRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AppShell } from "../routes/AppShell";
import { AppErrorBoundary } from "./AppErrorBoundary";
import { initPlenipoWebAuth, type InitWebAuthOptions, type WebAuthState } from "../lib/initWebAuth";
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
  /**
   * Where the API is and how to authenticate against it. Mirrors `PlenipoMobileApp`'s `config` prop, so
   * the two shells are configured the same way.
   *
   * Omitted, the shell asks the host (`GET /api/platform/auth-config`) and wires the built-in browser
   * OIDC adapter when a real authority is configured, or the dev-auth headers when one is not — which is
   * what lets one prebuilt bundle serve every deployment. Pass `auth` to bring your own identity stack.
   */
  config?: InitWebAuthOptions;
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
export function PlenipoApp({ moduleUi, branding, queryClient, config }: PlenipoAppProps) {
  // Create the default client once per mount (never on re-render) so the cache is stable.
  const [client] = useState(
    () =>
      queryClient ??
      new QueryClient({
        defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
      }),
  );

  // Resolve authentication before the shell mounts: AppShell fetches modules on mount, and a request
  // issued before the client knows how this deployment authenticates would carry the dev-auth headers.
  const [auth, setAuth] = useState<WebAuthState | null>(null);
  useEffect(() => {
    initPlenipoWebAuth(config ?? {})
      .then(setAuth)
      .catch(() => setAuth({ config: { mode: "dev" } }));
  }, [config]);

  if (!auth) {
    return null;
  }

  if (auth.error) {
    return (
      <AppErrorBoundary>
        <div role="alert" style={{ maxWidth: "40rem", margin: "4rem auto", padding: "0 1rem" }}>
          <h1>Sign-in is not available</h1>
          <p>{auth.error}</p>
        </div>
      </AppErrorBoundary>
    );
  }

  return (
    // Outermost guard: a crash in the providers, router, or shell shows a recovery screen, not a blank page.
    // (A module tab's own crash is contained closer in by TabErrorBoundary and never reaches this.)
    <AppErrorBoundary>
      <QueryClientProvider client={client}>
        <BrowserRouter>
          <AppShell moduleUi={moduleUi} branding={branding} />
        </BrowserRouter>
      </QueryClientProvider>
    </AppErrorBoundary>
  );
}
