/**
 * Dev auth headers sent on every API request and on the SignalR connection.
 * These stand in for real OIDC until it is wired up. Adjust freely for local
 * testing against the Plenipo API.
 */
export const devAuthHeaders: Record<string, string> = {
  "X-Dev-Subject": "dev-user",
  "X-Dev-Tenant": "dev",
  "X-Dev-Roles": "system_admin",
  "X-Dev-Name": "Dev User",
};

/**
 * Resolve the API base URL, stripping any trailing slash(es) so `${API_BASE}${path}` (used by the api
 * client, AG-UI, and the SignalR URL builder) never produces a doubled slash — a common footgun when a
 * host sets `VITE_API_BASE` with a trailing "/". Exported for unit testing.
 */
export function normalizeApiBase(raw: string | undefined): string {
  return (raw ?? "http://localhost:8080").replace(/\/+$/, "");
}

export const API_BASE: string = normalizeApiBase(import.meta.env.VITE_API_BASE);
