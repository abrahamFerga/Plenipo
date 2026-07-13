import {
  HubConnection,
  HubConnectionBuilder,
  LogLevel,
} from "@microsoft/signalr";
import { API_BASE, devAuthHeaders } from "./devAuth";

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
 * The agent hub URL, with dev-auth values forwarded as query-string params because the browser WebSocket
 * handshake cannot set custom headers (the server reads either). Exported for unit testing.
 */
export function agentHubUrl(): string {
  const url = new URL(`${API_BASE}/hubs/agent`);
  for (const [key, value] of Object.entries(devAuthHeaders)) {
    url.searchParams.set(key, value);
  }
  return url.toString();
}

/** Build (but do not start) a SignalR connection to the agent hub. */
export function createAgentConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(agentHubUrl(), {
      headers: devAuthHeaders,
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
