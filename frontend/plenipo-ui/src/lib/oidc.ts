import type { AuthAdapter } from "./webAuth";

/**
 * OIDC Authorization Code + PKCE for a browser, with no dependencies.
 *
 * Hand-rolled rather than taking `oidc-client-ts`, for four reasons that all point the same way:
 * this package's runtime dependencies are React and the manifest renderer, and a shipped library
 * inherits every consumer's audit surface; the flow needed here is one endpoint discovery, one
 * redirect, one code exchange and one refresh, which is ~200 lines of `crypto.subtle`; the repo's
 * pnpm workspace runs a minimum-release-age gate with a hand-maintained exclusion list, so adding a
 * dependency is a deliberate act with ongoing cost; and the platform already implements this exact
 * PKCE derivation server-side for connector OAuth, so the algorithm is one the codebase owns.
 *
 * What this does NOT do, on purpose: validate the ID token. The API validates the ACCESS token
 * against the authority's JWKS on every request, which is the only validation that decides anything.
 * A browser-side check of a token the browser also fetched proves nothing an attacker could not fake,
 * and inviting one invites getting it subtly wrong.
 */

/** Where the shell returns after the authority redirects back. Per surface, since /admin is scoped. */
const DEFAULT_REDIRECT_PATH = "/signin-callback";

const VERIFIER_KEY = "plenipo.oidc.verifier";
const STATE_KEY = "plenipo.oidc.state";
const NONCE_KEY = "plenipo.oidc.nonce";
const RETURN_KEY = "plenipo.oidc.return";
const REFRESH_KEY = "plenipo.oidc.refreshToken";

/** Refresh this many seconds before the token actually expires, to cover clock skew and latency. */
const REFRESH_SKEW_SECONDS = 60;

export interface OidcOptions {
  /** OIDC issuer, e.g. `https://tenant.ciamlogin.com/<id>/v2.0`. */
  authority: string;
  /** The PUBLIC client id. No secret is used or accepted — a browser cannot keep one. */
  clientId: string;
  /** Space-separated extra scopes. `openid profile email` are always requested. */
  scopes?: string | null;
  /** Path the authority redirects back to. Must be registered with the IdP. */
  redirectPath?: string;
}

interface DiscoveryDocument {
  authorization_endpoint: string;
  token_endpoint: string;
  end_session_endpoint?: string;
}

interface TokenResponse {
  access_token: string;
  refresh_token?: string;
  expires_in?: number;
}

function base64Url(bytes: ArrayBuffer | Uint8Array): string {
  const view = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = "";
  for (const byte of view) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function randomUrlSafe(byteLength = 32): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

/** RFC 7636 S256: challenge = base64url(SHA-256(ASCII(verifier))). */
async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64Url(digest);
}

/** True when the current URL is an authority redirect carrying a code (or an error). */
export function isSignInCallback(redirectPath: string = DEFAULT_REDIRECT_PATH): boolean {
  if (typeof window === "undefined") return false;
  if (window.location.pathname !== redirectPath) return false;
  const params = new URLSearchParams(window.location.search);
  return params.has("code") || params.has("error");
}

/**
 * An {@link AuthAdapter} backed by the given authority. Returns null from `getAccessToken` until a
 * sign-in completes, which in `oidc` mode means requests carry no credentials and the API answers a
 * clean 401 — the state the shell turns into a Sign in button.
 */
export function createOidcAuth(options: OidcOptions): AuthAdapter {
  const redirectPath = options.redirectPath ?? DEFAULT_REDIRECT_PATH;
  const scope = ["openid", "profile", "email", ...(options.scopes ?? "").split(/\s+/)]
    .filter((s, i, all) => s.length > 0 && all.indexOf(s) === i)
    .join(" ");

  let accessToken: string | null = null;
  let expiresAt = 0;
  let discovery: Promise<DiscoveryDocument> | null = null;
  // Single-flight: ten components mounting at once must not mint ten refreshes, which with rotating
  // refresh tokens would invalidate each other and sign the user out.
  let inFlight: Promise<string | null> | null = null;

  const redirectUri = () =>
    typeof window === "undefined" ? redirectPath : `${window.location.origin}${redirectPath}`;

  async function discover(): Promise<DiscoveryDocument> {
    discovery ??= (async () => {
      const base = options.authority.replace(/\/+$/, "");
      const res = await fetch(`${base}/.well-known/openid-configuration`, {
        headers: { Accept: "application/json" },
      });
      if (!res.ok) {
        throw new Error(`OIDC discovery failed for ${base} (${res.status}).`);
      }
      return (await res.json()) as DiscoveryDocument;
    })();

    try {
      return await discovery;
    } catch (error) {
      discovery = null; // a transient failure must not poison every later attempt
      throw error;
    }
  }

  function store(tokens: TokenResponse): string {
    accessToken = tokens.access_token;
    expiresAt = Date.now() + Math.max(0, (tokens.expires_in ?? 3600) - REFRESH_SKEW_SECONDS) * 1000;
    if (tokens.refresh_token) {
      sessionStorage.setItem(REFRESH_KEY, tokens.refresh_token);
    }
    return tokens.access_token;
  }

  function forget(): void {
    accessToken = null;
    expiresAt = 0;
    sessionStorage.removeItem(REFRESH_KEY);
  }

  async function exchange(body: Record<string, string>): Promise<TokenResponse> {
    const { token_endpoint } = await discover();
    const res = await fetch(token_endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
      // No client_secret: this is a public client, and a secret shipped to a browser is not a secret.
      body: new URLSearchParams({ client_id: options.clientId, ...body }).toString(),
    });
    if (!res.ok) {
      throw new Error(`OIDC token request failed (${res.status}).`);
    }
    return (await res.json()) as TokenResponse;
  }

  async function refresh(): Promise<string | null> {
    const refreshToken = sessionStorage.getItem(REFRESH_KEY);
    if (!refreshToken) return null;

    try {
      return store(await exchange({ grant_type: "refresh_token", refresh_token: refreshToken }));
    } catch {
      // An expired or already-rotated refresh token is a normal end of session, not an error to
      // surface: drop it and let the shell offer sign-in again.
      forget();
      return null;
    }
  }

  async function signIn(): Promise<void> {
    const verifier = randomUrlSafe();
    const state = randomUrlSafe(16);
    const nonce = randomUrlSafe(16);

    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);
    sessionStorage.setItem(NONCE_KEY, nonce);
    sessionStorage.setItem(RETURN_KEY, window.location.pathname + window.location.search);

    const { authorization_endpoint } = await discover();
    const params = new URLSearchParams({
      response_type: "code",
      client_id: options.clientId,
      redirect_uri: redirectUri(),
      scope,
      state,
      nonce,
      code_challenge: await codeChallenge(verifier),
      code_challenge_method: "S256",
    });

    window.location.assign(`${authorization_endpoint}?${params.toString()}`);
  }

  /**
   * Completes a redirect back from the authority. Returns where the user was before signing in, so
   * the caller can restore it. Every single-use value is consumed before anything can throw.
   */
  async function completeSignIn(): Promise<string> {
    const params = new URLSearchParams(window.location.search);

    const verifier = sessionStorage.getItem(VERIFIER_KEY);
    const expectedState = sessionStorage.getItem(STATE_KEY);
    const returnTo = sessionStorage.getItem(RETURN_KEY) ?? "/";
    sessionStorage.removeItem(VERIFIER_KEY);
    sessionStorage.removeItem(STATE_KEY);
    sessionStorage.removeItem(NONCE_KEY);
    sessionStorage.removeItem(RETURN_KEY);

    const error = params.get("error");
    if (error) {
      throw new Error(`Sign-in was refused by the identity provider: ${error}.`);
    }

    const code = params.get("code");
    if (!code || !verifier) {
      throw new Error("Sign-in callback is missing its authorization code.");
    }

    // The state check is what makes the callback un-forgeable by another site, and consuming the
    // values above is what makes a replay of this URL fail rather than mint a second session.
    if (!expectedState || params.get("state") !== expectedState) {
      throw new Error("Sign-in state did not match; the callback was not the one this app started.");
    }

    store(await exchange({
      grant_type: "authorization_code",
      code,
      redirect_uri: redirectUri(),
      code_verifier: verifier,
    }));

    return returnTo;
  }

  const adapter: AuthAdapter & { completeSignIn(): Promise<string> } = {
    getAccessToken() {
      if (accessToken && Date.now() < expiresAt) {
        return Promise.resolve(accessToken);
      }

      inFlight ??= refresh().finally(() => {
        inFlight = null;
      });
      return inFlight;
    },

    signIn,

    async signOut() {
      forget();
      const { end_session_endpoint } = await discover().catch(() => ({}) as DiscoveryDocument);
      if (end_session_endpoint) {
        window.location.assign(
          `${end_session_endpoint}?${new URLSearchParams({
            client_id: options.clientId,
            post_logout_redirect_uri: window.location.origin,
          }).toString()}`,
        );
      }
    },

    completeSignIn,
  };

  return adapter;
}

/** The extra member {@link createOidcAuth} adds — the callback handler an entry point calls. */
export type OidcAuthAdapter = AuthAdapter & { completeSignIn(): Promise<string> };
