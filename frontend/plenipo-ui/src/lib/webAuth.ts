import {
  configureClient,
  devAuthHeaders,
  normalizeApiBase,
  type AuthConfig,
} from "@plenipo/client";

/**
 * How the web shell authenticates to the API.
 *
 * The same concept as `@plenipo/mobile`'s `AuthAdapter`, member for member, so a product that has
 * wired one already knows this one. The platform had solved this shape for React Native and left the
 * browser without it: `PlenipoClientConfig.authHeaders` was the right seam, but overriding it presumed
 * the host already HAD a token, and nothing in the web shell obtained one — no sign-in route, no
 * authority redirect, no callback handler, no token store, no refresh, no 401 recovery. A product could
 * not supply what it had no way to acquire, so a correctly configured `Auth:Authority` deployment was
 * unreachable from a browser.
 *
 * `createOidcAuth` in `./oidc` implements this against any OIDC provider with no dependencies. A product
 * with its own identity stack implements it directly instead.
 */
export interface AuthAdapter {
  /** Called before every request, so a refreshed token is picked up without a reload. */
  getAccessToken(): Promise<string | null>;
  /** Interactive sign-in. The shell offers it when the API answers 401. */
  signIn?(): Promise<void>;
  /** Sign-out. The shell clears the stored token around this. */
  signOut?(): Promise<void>;
}

/**
 * Where a token lives between page loads. Mirrors the mobile `SecureStorageAdapter`.
 *
 * A browser has no keystore, so the default is `sessionStorage`: cleared when the tab closes, not
 * shared across tabs, and not readable by another origin. It is still readable by script running on
 * this origin — which is the honest trade a browser SPA makes, and why the access token itself is kept
 * in memory and only the refresh token is persisted.
 */
export interface SecureStorageAdapter {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

/** The no-IdP adapter: never produces a token, so requests fall back to dev auth in `dev` mode. */
export function devAuthOnly(): AuthAdapter {
  return { getAccessToken: () => Promise.resolve(null) };
}

/** In-memory storage. Correct for tests and for a shell that should forget on reload. */
export function memoryStorage(): SecureStorageAdapter {
  const map = new Map<string, string>();
  return {
    getItem: (key) => Promise.resolve(map.get(key) ?? null),
    setItem: (key, value) => {
      map.set(key, value);
      return Promise.resolve();
    },
    removeItem: (key) => {
      map.delete(key);
      return Promise.resolve();
    },
  };
}

/** `sessionStorage`, degrading to memory where it is unavailable (SSR, a locked-down browser). */
export function sessionStorageAdapter(): SecureStorageAdapter {
  try {
    if (typeof sessionStorage === "undefined") return memoryStorage();
    // Touch it: Safari in private mode throws on write rather than on access.
    const probe = "__plenipo_probe__";
    sessionStorage.setItem(probe, "1");
    sessionStorage.removeItem(probe);
  } catch {
    return memoryStorage();
  }

  return {
    getItem: (key) => Promise.resolve(sessionStorage.getItem(key)),
    setItem: (key, value) => {
      sessionStorage.setItem(key, value);
      return Promise.resolve();
    },
    removeItem: (key) => {
      sessionStorage.removeItem(key);
      return Promise.resolve();
    },
  };
}

const ACCESS_TOKEN_KEY = "plenipo.accessToken";

export interface PlenipoWebConfig {
  /** API origin. Defaults to whatever the shell already configured. */
  apiBase?: string;
  /** How to get a token. Defaults to {@link devAuthOnly}. */
  auth?: AuthAdapter;
  /** Where the last good token rests between page loads. Defaults to {@link sessionStorageAdapter}. */
  storage?: SecureStorageAdapter;
  /**
   * `"oidc"` when the deployment has a real IdP, `"dev"` otherwise — normally taken straight from
   * {@link AuthConfig.mode}. THIS IS THE SAFETY SWITCH: in `"oidc"` mode a null token sends NO headers
   * at all, so a signed-out browser gets a clean 401 it can recover from. Sending the dev-auth headers
   * there would put `X-Dev-Roles: system_admin` on the wire of a secured deployment, which is precisely
   * what the issue asked never to happen — inert against the server, but a lie on every request and the
   * first thing a security review would (rightly) fail.
   */
  mode?: AuthConfig["mode"];
}

let activeAuth: AuthAdapter = devAuthOnly();
let activeStorage: SecureStorageAdapter = memoryStorage();
let activeMode: AuthConfig["mode"] = "dev";

/**
 * Point the web shell at an API and tell it how to authenticate. Call once at startup, before
 * rendering. Everything is optional and every default is the behaviour the shell has always had.
 */
export function configurePlenipoWeb(config: PlenipoWebConfig = {}): void {
  activeAuth = config.auth ?? devAuthOnly();
  activeStorage = config.storage ?? sessionStorageAdapter();
  activeMode = config.mode ?? "dev";
  const mode = activeMode;

  configureClient({
    ...(config.apiBase === undefined ? {} : { baseUrl: normalizeApiBase(config.apiBase) }),

    // Asked per request, so a token refreshed mid-session is picked up without a reload.
    authHeaders: async () => {
      const token = await activeAuth.getAccessToken();
      if (token) {
        await activeStorage.setItem(ACCESS_TOKEN_KEY, token);
        return { Authorization: `Bearer ${token}` };
      }

      // A token stored on a previous load still counts while the adapter is refreshing.
      const stored = await activeStorage.getItem(ACCESS_TOKEN_KEY);
      if (stored) {
        return { Authorization: `Bearer ${stored}` };
      }

      return mode === "oidc" ? {} : { ...devAuthHeaders };
    },
  });
}

/** The active adapter — for the shell's 401 handling and the Sign in / Sign out controls. */
export function plenipoWebAuth(): AuthAdapter {
  return activeAuth;
}

/**
 * Whether this deployment has a real IdP. Read synchronously by callers that cannot await, notably
 * the SignalR connection builder, which must decide whether dev headers belong on the wire.
 */
export function plenipoWebMode(): AuthConfig["mode"] {
  return activeMode;
}

/** Signs out and forgets the stored token. Safe to call when the adapter offers no `signOut`. */
export async function signOutPlenipoWeb(): Promise<void> {
  await activeStorage.removeItem(ACCESS_TOKEN_KEY);
  await activeAuth.signOut?.();
}

/** Restores the defaults. For tests. */
export function resetPlenipoWeb(): void {
  activeAuth = devAuthOnly();
  activeStorage = memoryStorage();
  activeMode = "dev";
}
