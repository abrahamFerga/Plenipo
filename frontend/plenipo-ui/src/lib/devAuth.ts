import { configureClient, devAuthHeaders, normalizeApiBase } from "@plenipo/client";

/**
 * This app's half of the shared client's configuration: where the API is. Everything else — the
 * fetch transport, the credentials, the form-default sources — keeps @plenipo/client's browser
 * defaults, which is exactly what this shell has always done.
 *
 * Runs as an import side effect, and every `lib/*` shim imports this module first, so the client
 * is pointed at the right origin before any component can issue a request.
 */
configureClient({ baseUrl: import.meta.env.VITE_API_BASE });

/**
 * Dev auth headers sent on every API request and on the SignalR connection. These stand in for
 * real OIDC until it is wired up. Re-exported from @plenipo/client so the SignalR URL builder
 * (which needs them synchronously, as query-string parameters) and the shared fetch helpers can
 * never disagree about what a dev session looks like.
 */
export { devAuthHeaders, normalizeApiBase };

/** The resolved API base URL — for the SignalR hub URL and the "can't reach the API" screens. */
export const API_BASE: string = normalizeApiBase(import.meta.env.VITE_API_BASE);
