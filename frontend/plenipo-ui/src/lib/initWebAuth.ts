import { fetchAuthConfig, type AuthConfig } from "@plenipo/client";
import { createOidcAuth, isSignInCallback, type OidcAuthAdapter } from "./oidc";
import { configurePlenipoWeb, devAuthOnly, sessionStorageAdapter, type AuthAdapter } from "./webAuth";

export interface InitWebAuthOptions {
  /** API origin. Defaults to whatever the shell already resolved. */
  apiBase?: string;
  /**
   * Path the authority redirects back to. `/admin` scopes its own fallback route, so the console
   * passes `/admin/signin-callback` while the app surface keeps the default.
   */
  redirectPath?: string;
  /** Supply an adapter instead of deriving one — a product with its own identity stack. */
  auth?: AuthAdapter;
}

export interface WebAuthState {
  config: AuthConfig;
  /** Set when the deployment says `oidc` but cannot be signed into — render it, do not swallow it. */
  error?: string;
}

/**
 * Ask the host how to authenticate, wire the matching adapter, and finish a sign-in redirect if this
 * page load is one. **Await this before rendering anything that calls the API** — the shell issues its
 * first request on mount, and a request sent before the client is configured would carry the dev-auth
 * headers, which is exactly what a secured deployment must never see.
 *
 * Every failure lands on `dev`, the behaviour the shell has always had, so a local host with nothing
 * configured still works with no configuration at all.
 */
export async function initPlenipoWebAuth(options: InitWebAuthOptions = {}): Promise<WebAuthState> {
  const config = await fetchAuthConfig(options.apiBase);

  if (options.auth) {
    configurePlenipoWeb({
      apiBase: options.apiBase,
      auth: options.auth,
      storage: sessionStorageAdapter(),
      mode: config.mode,
    });
    return { config };
  }

  if (config.mode !== "oidc") {
    configurePlenipoWeb({ apiBase: options.apiBase, auth: devAuthOnly(), mode: "dev" });
    return { config };
  }

  if (!config.authority || !config.clientId) {
    // Deliberately not a startup failure on the server (that would break every existing API-only
    // deployment on upgrade), so it has to be a visible failure here instead of a silent 401 loop.
    configurePlenipoWeb({ apiBase: options.apiBase, auth: devAuthOnly(), mode: "oidc" });
    return {
      config,
      error:
        "This deployment requires sign-in but does not publish a client id. Set Auth:ClientId (and " +
        "Auth:Authority) on the host, and register this app's redirect URI with the identity provider.",
    };
  }

  const redirectPath = options.redirectPath;
  const adapter = createOidcAuth({
    authority: config.authority,
    clientId: config.clientId,
    scopes: config.scopes,
    ...(redirectPath ? { redirectPath } : {}),
  }) as OidcAuthAdapter;

  configurePlenipoWeb({
    apiBase: options.apiBase,
    auth: adapter,
    storage: sessionStorageAdapter(),
    mode: "oidc",
  });

  if (isSignInCallback(redirectPath)) {
    try {
      const returnTo = await adapter.completeSignIn();
      // Replace, never push: the callback URL carries a single-use code, and leaving it in history
      // means Back replays a request the authority will refuse.
      window.history.replaceState(null, "", returnTo);
    } catch (error) {
      window.history.replaceState(null, "", "/");
      return { config, error: error instanceof Error ? error.message : "Sign-in could not be completed." };
    }
  }

  return { config };
}
