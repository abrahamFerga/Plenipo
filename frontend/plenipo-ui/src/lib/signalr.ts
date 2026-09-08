import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel,
} from "@microsoft/signalr";
import { clientConfig } from "@plenipo/client";
import { API_BASE, devAuthHeaders } from "./devAuth";
import { plenipoWebMode } from "./webAuth";

/** Request payload sent to the hub's streaming `Stream` method. */
export interface AgentStreamRequest {
  moduleId: string;
  conversationId?: string;
  message: string;
}

/** Events streamed back from the agent hub. */
export interface AgentStreamEvent {
  type:
    | "Token"
    | "ToolInvoked"
    | "Completed"
    | "Error"
    | "Usage"
    | "ApprovalRequired";
  text?: string;
  toolName?: string;
  conversationId?: string;
  error?: string;
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}

/**
 * The agent hub URL. Exported for unit testing.
 *
 * It used to carry the dev-auth values as query-string parameters, on the stated grounds that "the
 * server reads either". It does not: nothing in the platform reads `Request.Query` for identity. So the
 * parameters authenticated nothing, and on a secured deployment they would have put
 * `X-Dev-Roles: system_admin` into every hub URL — in browser history, proxy logs and error reports.
 */
export function agentHubUrl(): string {
  return `${API_BASE}/hubs/agent`;
}

/**
 * Build (but do not start) a SignalR connection to the agent hub.
 *
 * Credentials come from the configured client, so whatever `configurePlenipoWeb` decided — a bearer
 * token or the dev-auth headers — is what the hub gets, and the two can never disagree.
 *
 * A browser's WebSocket handshake cannot set headers, so the bearer goes through
 * `accessTokenFactory`: SignalR sends it as an `Authorization` header on the negotiate and on the
 * long-polling/SSE transports, and as an `access_token` query parameter on WebSockets — which the host
 * reads back in `AuthSetup` for `/hubs` paths only. The non-bearer dev headers stay headers: they are
 * Development-only, where no WebSocket-vs-header distinction matters because the dev handler already
 * defaults every value.
 */
export function createAgentConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(agentHubUrl(), {
      accessTokenFactory: async () => {
        const headers = await clientConfig().authHeaders();
        const bearer = headers["Authorization"] ?? headers["authorization"];
        return bearer?.startsWith("Bearer ") ? bearer.slice("Bearer ".length) : "";
      },
      headers: agentHubHeaders(),
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}

/**
 * The header bag a dev-mode hub connection carries — the configured client's own identity, the
 * same one every HTTP request sends, so a product's dev-identity picker reaches the hub too (#215).
 * It used to be the shared `devAuthHeaders` constant regardless of `configureClient`, which made
 * every chat turn run as the constant's `system_admin` while the REST calls ran as whoever was
 * picked — the split identity #110 fixed on the server, recreated in the client.
 *
 * `withUrl` reads the bag synchronously, so a synchronous `authHeaders` is read directly and an
 * async one falls back to the constant (a product whose picker must reach the hub keeps it
 * synchronous). A bearer never travels here — that is `accessTokenFactory`'s job, above — and a
 * secured deployment (`mode: "oidc"`) sends no dev header at all.
 */
export function agentHubHeaders(): Record<string, string> {
  if (plenipoWebMode() === "oidc") {
    return {};
  }

  const configured = clientConfig().authHeaders();
  const isPromise = typeof (configured as Promise<unknown> | undefined)?.then === "function";
  const source = isPromise ? devAuthHeaders : (configured as Record<string, string>);
  const headers: Record<string, string> = {};
  for (const [name, value] of Object.entries(source)) {
    if (name.toLowerCase() !== "authorization") {
      headers[name] = value;
    }
  }
  return headers;
}
