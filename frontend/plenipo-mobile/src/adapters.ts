import type { FetchLike } from "@plenipo/client";

/**
 * The platform capabilities this shell needs but refuses to import.
 *
 * Secure storage, push, device identity and locale all come from native modules, and a library
 * that imports them decides for every product which ones it must install, which Expo SDK it must
 * be on, and how it must authenticate. So the shell takes them as adapters instead: the core has
 * no native dependency at all, runs in a plain test renderer, and a product swaps any one of them
 * (a corporate MDM push gateway, an existing keychain wrapper) without forking anything.
 *
 * The ordinary path is one line — `@plenipo/mobile/expo` implements all of these on the standard
 * Expo modules. These interfaces are what that line satisfies.
 */

/** Where a bearer token lives between launches. Must be the OS keystore, never plain storage. */
export interface SecureStorageAdapter {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

/**
 * How the app authenticates to the API.
 *
 * Returning null is a supported answer, not a failure: the shell then sends the platform's
 * dev-auth headers, which is what makes a brand-new app work against a local host with no IdP,
 * no client id, and no configuration — the same keyless-by-default rule the backend follows. A
 * production app returns a real token here (expo-auth-session, MSAL, whatever the product uses).
 */
export interface AuthAdapter {
  /** Called before every request, so a refreshed token is picked up without a restart. */
  getAccessToken(): Promise<string | null>;
  /** Optional interactive sign-in, offered by the shell when the API answers 401. */
  signIn?(): Promise<void>;
  /** Optional sign-out. The shell also forgets the push device registration around this. */
  signOut?(): Promise<void>;
}

/** Which push service a token belongs to. Mirrors the platform's `DevicePlatform`. */
export type MobilePlatform = "Ios" | "Android" | "Web";

/** Identity of this installation, for push registration and the "your devices" list. */
export interface DeviceAdapter {
  /**
   * A stable id for this installation that survives push-token rotation. Minted once and kept —
   * it is what makes re-registering on every launch an update instead of a new row.
   */
  installationId(): Promise<string>;
  /** Human-readable label ("Pixel 9"), or null when the platform won't say. */
  deviceName(): string | null;
  platform(): MobilePlatform;
}

/**
 * Push registration and delivery. Optional: an app that doesn't want notifications simply omits
 * it, and the shell never asks the OS for permission it isn't going to use.
 */
export interface PushAdapter {
  /**
   * Ask the OS for notification permission and return a push token. Returns null when the user
   * declines or the platform can't issue one (a simulator, for instance) — a declined permission
   * is a normal outcome and must not break startup.
   */
  getPushToken(): Promise<string | null>;

  /**
   * Subscribe to notification taps. The handler receives the notification's `link` — the same
   * app-relative link the in-app inbox row carries — which the shell resolves against the module
   * manifest's tab routes. Returns an unsubscribe function.
   */
  onNotificationTapped(handler: (link: string | null) => void): () => void;
}

/**
 * What the viewer's device knows about where they are — the answers to a manifest field's
 * `defaultFrom` tokens, which a server-side manifest cannot possibly know.
 */
export interface LocaleAdapter {
  /** IANA time zone, e.g. "America/Mexico_City". */
  timeZone(): string | undefined;
  /** ISO-4217 currency guessed from the device region, e.g. "MXN". */
  currency(): string | undefined;
}

/** The full set. Only storage, auth, and device are required; the rest degrade to doing nothing. */
export interface PlenipoAdapters {
  storage: SecureStorageAdapter;
  auth: AuthAdapter;
  device: DeviceAdapter;
  push?: PushAdapter;
  locale?: LocaleAdapter;
  /**
   * The fetch used for every API call. React Native's built-in fetch cannot stream a response
   * body, which the AG-UI chat transport needs, so the Expo adapters supply `expo/fetch`.
   */
  fetch?: FetchLike;
}

/**
 * In-memory storage. Correct for tests and for a web preview; wrong for a real device, where a
 * bearer token must sit in the keystore. The Expo adapters replace it with SecureStore.
 */
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

/**
 * The no-IdP auth adapter: never produces a token, so every request falls back to dev-auth. This
 * is the default, and it is why a freshly generated app talks to a local host immediately.
 */
export function devAuthOnly(): AuthAdapter {
  return { getAccessToken: () => Promise.resolve(null) };
}
