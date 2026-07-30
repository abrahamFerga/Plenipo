import { configureClient, currencyForLocale, devAuthHeaders } from "@plenipo/client";
import {
  devAuthOnly,
  memoryStorage,
  type AuthAdapter,
  type DeviceAdapter,
  type LocaleAdapter,
  type PlenipoAdapters,
  type PushAdapter,
  type SecureStorageAdapter,
} from "./adapters";

/** Where the shell keeps the bearer token, when an auth adapter produces one. */
export const ACCESS_TOKEN_KEY = "plenipo.accessToken";

/** Where the installation id is kept, so it survives token rotation and reinstalls of the token. */
export const INSTALLATION_ID_KEY = "plenipo.installationId";

/** What an app hands the shell at startup. */
export interface PlenipoMobileConfig extends PlenipoAdapters {
  /** Origin of the Plenipo API, e.g. "https://api.acme.com". Trailing slashes are fine. */
  apiBase: string;
}

interface ResolvedConfig {
  apiBase: string;
  storage: SecureStorageAdapter;
  auth: AuthAdapter;
  device: DeviceAdapter | null;
  push: PushAdapter | null;
  locale: LocaleAdapter | null;
}

let resolved: ResolvedConfig | null = null;

/**
 * Point the shell at an API and give it its platform adapters. Call once, before rendering —
 * `PlenipoMobileApp` does it for you from its `config` prop.
 *
 * This is also where the shared @plenipo/client gets configured, so every request the renderer
 * makes carries the right origin, the right credentials, and a streaming-capable fetch.
 */
export function configurePlenipoMobile(config: PlenipoMobileConfig): void {
  const storage = config.storage ?? memoryStorage();
  const auth = config.auth ?? devAuthOnly();

  resolved = {
    apiBase: config.apiBase,
    storage,
    auth,
    device: config.device ?? null,
    push: config.push ?? null,
    locale: config.locale ?? null,
  };

  configureClient({
    baseUrl: config.apiBase,
    ...(config.fetch ? { fetch: config.fetch } : {}),

    // Asked per request, so a token refreshed mid-session is picked up without a restart. A null
    // token is not an error — it means this deployment has no IdP wired yet, and the platform's
    // Development-only dev auth takes over. That is what lets a new app talk to a local host
    // with nothing configured.
    authHeaders: async () => {
      const token = await auth.getAccessToken();
      if (token) {
        await storage.setItem(ACCESS_TOKEN_KEY, token);
        return { Authorization: `Bearer ${token}` };
      }

      // A token stored on a previous launch still counts while the adapter is refreshing.
      const stored = await storage.getItem(ACCESS_TOKEN_KEY);
      return stored ? { Authorization: `Bearer ${stored}` } : { ...devAuthHeaders };
    },

    // The manifest's `defaultFrom` tokens are named for a browser because that is what the C#
    // FieldDefaultSources calls them; on a phone the device answers them. Without a locale
    // adapter these fall through to the client's Intl-based defaults.
    ...(config.locale
      ? {
          fieldDefaultSources: {
            "browser-timezone": () => config.locale!.timeZone(),
            "browser-currency": () => config.locale!.currency(),
          },
        }
      : {}),
  });
}

/** The active configuration. Throws if the app rendered before configuring — a wiring bug. */
export function mobileConfig(): ResolvedConfig {
  if (resolved === null) {
    throw new Error(
      "Plenipo mobile is not configured. Render <PlenipoMobileApp config={…} /> or call " +
        "configurePlenipoMobile() before anything else.",
    );
  }
  return resolved;
}

/** True once configured. Lets a screen degrade instead of throwing during an odd mount order. */
export function isConfigured(): boolean {
  return resolved !== null;
}

/** Forgets the configuration. For tests. */
export function resetMobileConfig(): void {
  resolved = null;
}

/**
 * A locale adapter over whatever `Intl` the runtime has — the fallback used when an app supplies
 * none. Hermes ships Intl, so the time zone is usually right; the currency needs a region, which
 * `Intl` alone won't give on a device, so it stays undefined unless the app wires
 * expo-localization.
 */
export function intlLocale(): LocaleAdapter {
  return {
    timeZone: () => {
      try {
        return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
      } catch {
        return undefined;
      }
    },
    currency: () => {
      try {
        return currencyForLocale(Intl.DateTimeFormat().resolvedOptions().locale);
      } catch {
        return undefined;
      }
    },
  };
}
