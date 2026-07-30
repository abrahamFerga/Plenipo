/**
 * How this package reaches a Plenipo API.
 *
 * The client is deliberately renderer-agnostic: the React web shell, the React Native shell, a
 * CLI, or a test each supply their own transport and credentials, and nothing here assumes a
 * browser. Everything has a working default, so an unconfigured import behaves exactly like the
 * web shell always has — localhost, dev-auth headers, the global `fetch`.
 */

/**
 * The slice of `fetch` this package uses: an absolute URL string and an init bag.
 *
 * Declared rather than written as `typeof fetch` because that type is not the same everywhere —
 * the DOM lib's overload accepts `URL`, React Native's does not, and a config field typed
 * `typeof fetch` therefore fails to compile in whichever environment isn't the one it was written
 * against. Every URL here is built as a string anyway, so the narrow shape loses nothing and is
 * satisfied by the browser's fetch, React Native's, `expo/fetch`, and any test double.
 */
export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export interface PlenipoClientConfig {
  /** API origin, no trailing slash (see {@link normalizeApiBase}). */
  baseUrl: string;

  /**
   * Headers attached to every request. Called per request — so a mobile client can return a
   * freshly refreshed bearer token, and a web client can return a constant. May be async.
   */
  authHeaders: () => Record<string, string> | Promise<Record<string, string>>;

  /**
   * The fetch implementation. React Native's built-in `fetch` cannot stream a response body,
   * which the AG-UI chat transport requires — Expo apps pass `fetch` from `expo/fetch` here.
   */
  fetch: FetchLike;

  /**
   * Resolvers for {@link TabEditorField.defaultFrom} tokens — the starting values only the
   * VIEWER's client can supply, which a manifest declared at server startup cannot know. Keyed by
   * the token the C# `FieldDefaultSources` declares. Merged over the built-ins by
   * {@link configureClient}, so a platform (mobile) can improve one without restating the rest.
   */
  fieldDefaultSources: Record<string, () => string | undefined>;
}

/**
 * Dev-auth headers, the stand-in for real OIDC in local development. The server's dev
 * authentication handler is Development-only, so these are inert in production — a deployment
 * with a real IdP overrides {@link PlenipoClientConfig.authHeaders} with a bearer token.
 */
export const devAuthHeaders: Record<string, string> = {
  "X-Dev-Subject": "dev-user",
  "X-Dev-Tenant": "dev",
  "X-Dev-Roles": "system_admin",
  "X-Dev-Name": "Dev User",
};

/**
 * Resolve an API base URL, stripping any trailing slash(es) so `${baseUrl}${path}` never produces
 * a doubled slash — a common footgun when a host configures the base with a trailing "/".
 */
export function normalizeApiBase(raw: string | undefined): string {
  return (raw ?? "http://localhost:8080").replace(/\/+$/, "");
}

/**
 * The viewer's IANA time zone. Works anywhere with Intl — browsers and Hermes alike — and is
 * simply absent in a locked-down runtime, which is not worth throwing over.
 */
function clientTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}

// ISO-3166 region → ISO-4217 currency, for the currencies people commonly hold. This is a GUESS
// map, not a source of truth: a region with no entry simply yields no guess (the field stays on
// "Choose…"), and any guess is still validated against the field's options and remains the user's
// to change. Eurozone members all map to EUR.
const REGION_CURRENCY: Record<string, string> = {
  US: "USD", CA: "CAD", MX: "MXN", BR: "BRL", AR: "ARS", CL: "CLP", CO: "COP", PE: "PEN",
  GB: "GBP", IE: "EUR", FR: "EUR", DE: "EUR", ES: "EUR", PT: "EUR", IT: "EUR", NL: "EUR",
  BE: "EUR", AT: "EUR", FI: "EUR", GR: "EUR", LU: "EUR", SK: "EUR", SI: "EUR", EE: "EUR",
  LV: "EUR", LT: "EUR", CY: "EUR", MT: "EUR", HR: "EUR",
  CH: "CHF", SE: "SEK", NO: "NOK", DK: "DKK", PL: "PLN", CZ: "CZK", HU: "HUF", RO: "RON",
  BG: "BGN", IS: "ISK", TR: "TRY", RU: "RUB", UA: "UAH",
  CN: "CNY", JP: "JPY", KR: "KRW", IN: "INR", ID: "IDR", TH: "THB", VN: "VND", PH: "PHP",
  MY: "MYR", SG: "SGD", HK: "HKD", TW: "TWD", PK: "PKR", BD: "BDT", LK: "LKR",
  AU: "AUD", NZ: "NZD",
  ZA: "ZAR", NG: "NGN", EG: "EGP", KE: "KES", GH: "GHS", MA: "MAD",
  AE: "AED", SA: "SAR", IL: "ILS", QA: "QAR", KW: "KWD", BH: "BHD", OM: "OMR", JO: "JOD",
};

/** ISO-4217 currency for a BCP-47 language tag's region, or undefined when there's no good guess. */
export function currencyForLocale(tag: string | undefined): string | undefined {
  if (!tag) return undefined;
  try {
    // maximize() fills in the LIKELY region when the tag omits it: "es" → "es-ES", "en" → "en-US".
    const region = new Intl.Locale(tag).maximize().region;
    return region ? REGION_CURRENCY[region] : undefined;
  } catch {
    return undefined;
  }
}

/**
 * The currency of the viewer's locale region. On the web that's `navigator.language`; a runtime
 * without `navigator` (React Native) yields no guess here and overrides this source with a
 * platform locale API instead.
 */
function clientCurrency(): string | undefined {
  return currencyForLocale(typeof navigator !== "undefined" ? navigator.language : undefined);
}

/** The built-in `defaultFrom` resolvers. Keep the keys in sync with C# `FieldDefaultSources`. */
const BUILT_IN_SOURCES: Record<string, () => string | undefined> = {
  "browser-timezone": clientTimeZone,
  "browser-currency": clientCurrency,
};

let current: PlenipoClientConfig = {
  baseUrl: normalizeApiBase(undefined),
  authHeaders: () => devAuthHeaders,
  // Read through `globalThis` at call time rather than capturing the binding, so a runtime that
  // installs its fetch after this module loads (and test doubles that swap it) are both honoured.
  fetch: (input, init) => globalThis.fetch(input, init),
  fieldDefaultSources: BUILT_IN_SOURCES,
};

/**
 * Point the client at an API and tell it how to authenticate. Call once at startup, before any
 * request. Partial: unspecified keys keep their current value, and `fieldDefaultSources` merges
 * over the built-ins rather than replacing them.
 */
export function configureClient(config: Partial<PlenipoClientConfig>): void {
  current = {
    ...current,
    ...config,
    baseUrl: config.baseUrl === undefined ? current.baseUrl : normalizeApiBase(config.baseUrl),
    fieldDefaultSources: config.fieldDefaultSources
      ? { ...current.fieldDefaultSources, ...config.fieldDefaultSources }
      : current.fieldDefaultSources,
  };
}

/** The active configuration. */
export function clientConfig(): Readonly<PlenipoClientConfig> {
  return current;
}

/** The configured API origin — for building URLs the fetch helpers don't cover (downloads, hubs). */
export function apiBase(): string {
  return current.baseUrl;
}

/** Restores the defaults. For tests. */
export function resetClientConfig(): void {
  current = {
    baseUrl: normalizeApiBase(undefined),
    authHeaders: () => devAuthHeaders,
    fetch: (input, init) => globalThis.fetch(input, init),
    fieldDefaultSources: BUILT_IN_SOURCES,
  };
}
