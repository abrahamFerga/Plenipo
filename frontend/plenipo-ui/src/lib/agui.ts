import { API_BASE, devAuthHeaders } from "./devAuth";
import { ApiError } from "./api";

/**
 * A single AG-UI protocol event. The `type` is a SCREAMING_SNAKE_CASE event name
 * (RUN_STARTED, TEXT_MESSAGE_CONTENT, TOOL_CALL_START, CUSTOM, RUN_FINISHED, …).
 * Extra fields depend on the event type — see https://docs.ag-ui.com/.
 */
export interface AguiEvent {
  type: string;
  [key: string]: unknown;
}

/**
 * A client message id. Prefers `crypto.randomUUID` (a real UUID) but falls back to a simple unique id when
 * it's unavailable — `crypto.randomUUID` only exists in secure contexts, so a UI served over http on a
 * non-localhost origin would otherwise throw. Exported for testing.
 */
export function messageId(): string {
  return typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

/**
 * Drive Plenipo's AG-UI-compatible chat endpoint and yield protocol events as
 * they stream in. This talks the open AG-UI protocol (HTTP POST + SSE), so the
 * same code works against any AG-UI server — and Plenipo's implementation keeps
 * all per-user tool authorization, auditing, and token tracking server-side.
 *
 * Example:
 * ```ts
 * for await (const evt of runAgui("finance", "How much did I spend?")) {
 *   if (evt.type === "TEXT_MESSAGE_CONTENT") append(evt.delta as string);
 * }
 * ```
 */
export async function* runAgui(
  moduleId: string,
  message: string,
  options: { threadId?: string; signal?: AbortSignal; agent?: string; model?: string } = {},
): AsyncGenerator<AguiEvent> {
  // The composer's agent/model picks ride AG-UI's forwardedProps (the protocol's extension slot);
  // the server validates both against what it advertises.
  const forwardedProps =
    options.agent || options.model
      ? { agent: options.agent || undefined, model: options.model || undefined }
      : undefined;
  const res = await fetch(`${API_BASE}/api/agui/${moduleId}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "text/event-stream",
      ...devAuthHeaders,
    },
    body: JSON.stringify({
      threadId: options.threadId,
      messages: [{ id: messageId(), role: "user", content: message }],
      forwardedProps,
    }),
    signal: options.signal,
  });

  if (!res.ok || !res.body) {
    throw new ApiError(res.status, res.statusText, `AG-UI run failed: ${res.status} ${res.statusText}`);
  }

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    const { events, rest } = parseAguiFrames(buffer);
    buffer = rest;
    for (const event of events) {
      yield event;
    }
  }
}

/**
 * Parse complete SSE frames out of a buffer into AG-UI events, returning the events found and the
 * unconsumed tail (a partial frame still streaming). Exported for unit testing; `runAgui` feeds it each
 * decoded chunk. Tolerant by design so it works against any AG-UI server:
 *  - accepts LF or CRLF line endings (the SSE blank-line boundary is `\r?\n\r?\n`);
 *  - concatenates multiple `data:` lines per SSE, stripping one optional leading space;
 *  - skips frames with no data (e.g. keep-alive comments) and skips a malformed frame instead of
 *    aborting the whole stream (one bad event must not lose the rest of the response).
 */
export function parseAguiFrames(buffer: string): { events: AguiEvent[]; rest: string } {
  const events: AguiEvent[] = [];

  let match: RegExpExecArray | null;
  while ((match = /\r?\n\r?\n/.exec(buffer)) !== null) {
    const frame = buffer.slice(0, match.index);
    buffer = buffer.slice(match.index + match[0].length);

    const data = frame
      .split(/\r?\n/)
      .filter((line) => line.startsWith("data:"))
      .map((line) => line.slice(5).replace(/^ /, "")) // drop "data:" and one optional leading space
      .join("\n");

    if (data) {
      try {
        events.push(JSON.parse(data) as AguiEvent);
      } catch {
        // Malformed frame — skip it rather than aborting the stream.
      }
    }
  }

  return { events, rest: buffer };
}
