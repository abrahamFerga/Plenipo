import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { initTheme, initPlenipoWebAuth } from "@plenipo/ui";
import App from "./App";
import "./index.css";

initTheme();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});

// Authentication is resolved BEFORE the first render, not inside it: the console's very first screen
// queries the admin API, and a request issued before the client knows how this deployment authenticates
// would carry the dev-auth headers. /admin is the surface where that matters most.
//
// The console is served under /admin with its own router basename, so its redirect URI is scoped to
// match — the host's SPA fallback for /admin would not serve a bare /signin-callback.
// (Not top-level await: the build targets browsers that predate it.)
const root = createRoot(document.getElementById("root")!);

initPlenipoWebAuth({ redirectPath: "/admin/signin-callback" })
  .catch(() => ({ config: { mode: "dev" as const } }))
  .then((auth) =>
    root.render(
      <StrictMode>
        {"error" in auth && auth.error ? (
          <div role="alert" style={{ maxWidth: "40rem", margin: "4rem auto", padding: "0 1rem" }}>
            <h1>Sign-in is not available</h1>
            <p>{auth.error}</p>
          </div>
        ) : (
          <QueryClientProvider client={queryClient}>
            <App />
          </QueryClientProvider>
        )}
      </StrictMode>,
    ),
  );
