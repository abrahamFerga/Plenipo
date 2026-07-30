// ─────────────────────────────────────────────────────────────────────────────
// @plenipo/client — the renderer-free half of a Plenipo frontend
//
// Everything here is plain TypeScript: the client-side mirror of the platform's
// manifest contract (ModuleManifest / TabDescriptor and friends, as the API
// serializes them), the fetch helpers that talk to it, the AG-UI chat transport,
// and the small pieces of logic that must behave identically wherever a shell
// runs — permission matching, form defaults, attachment references.
//
// No React, no DOM, no bundler globals. That is the point: @plenipo/ui renders it
// with HTML and @plenipo/mobile renders it with native views, and a change to the
// C# descriptors lands in exactly one TypeScript file for both.
//
// Call `configureClient` once at startup to say where the API is and how to
// authenticate; every default works out of the box against a local dev host.
// ─────────────────────────────────────────────────────────────────────────────

// Where the API is, how to reach it, and how to answer the manifest's
// client-supplied form defaults.
export {
  configureClient,
  clientConfig,
  resetClientConfig,
  apiBase,
  normalizeApiBase,
  currencyForLocale,
  devAuthHeaders,
} from "./config";
export type { FetchLike, PlenipoClientConfig } from "./config";

// The manifest contract + the REST surface (types, fetch helpers, and the `api` facade).
export * from "./api";

// The AG-UI chat transport: HTTP POST + SSE, the same protocol any AG-UI client speaks.
export * from "./agui";

// Client-side mirror of the server's PermissionMatcher — shapes UI only; the API still enforces.
export * from "./permissions";

// What a server-declared form field starts as.
export * from "./fieldDefaults";

// The plain-text attachment convention chat messages carry across every channel.
export * from "./attachments";

// Turning a tab's rows into plottable series — the numbers, not the pixels.
export * from "./charts";

// Reading a tab's rows: endpoint templates, column fallback, PII masking.
export * from "./rows";
