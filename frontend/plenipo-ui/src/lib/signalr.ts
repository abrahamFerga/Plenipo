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
      // Dev auth only, and only where it is live. `withUrl` reads this synchronously while
      // `authHeaders` may be async, so it cannot be derived from there — but the dev headers are a
      // constant, and a secured deployment must send none of them at all.
      headers: plenipoWebMode() === "oidc" ? {} : { ...devAuthHeaders },
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
