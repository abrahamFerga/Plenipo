// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ChatPanel } from "./ChatPanel";

/** Builds an SSE Response streaming the given AG-UI events. */
function sseResponse(events: object[]): Response {
  const body = events.map((e) => `data: ${JSON.stringify(e)}\n\n`).join("");
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(new TextEncoder().encode(body));
      controller.close();
    },
  });
  return { ok: true, status: 200, statusText: "OK", body: stream } as unknown as Response;
}

function jsonResponse(payload: unknown): Response {
  return { ok: true, status: 200, json: () => Promise.resolve(payload) } as unknown as Response;
}

/**
 * The DEFAULT transport: the panel drives the open AG-UI protocol (POST /api/agui/{module} + SSE)
 * that the backend implements — deltas render as they stream, tool calls and usage surface, and
 * RUN_FINISHED's conversation id becomes the thread id for the next turn (resume-by-id).
 */
describe("ChatPanel over AG-UI", () => {
  beforeAll(() => {
    Element.prototype.scrollTo = vi.fn() as unknown as typeof Element.prototype.scrollTo;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  function renderAgui(fetchMock: ReturnType<typeof vi.fn>) {
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ChatPanel moduleId="finance" />
      </QueryClientProvider>,
    );
  }

  function aguiAwareFetch(events: object[]) {
    return vi.fn((input: RequestInfo | URL, _init?: RequestInit) =>
      Promise.resolve(
        String(input).includes("/api/agui/finance") ? sseResponse(events) : jsonResponse({ permissions: [] }),
      ),
    );
  }

  it("streams a turn over the AG-UI protocol and threads the conversation id", async () => {
    const fetchMock = aguiAwareFetch([
      { type: "RUN_STARTED", threadId: "t", runId: "r" },
      { type: "TEXT_MESSAGE_START", messageId: "m", role: "assistant" },
      { type: "TEXT_MESSAGE_CONTENT", messageId: "m", delta: "You spent " },
      { type: "TEXT_MESSAGE_CONTENT", messageId: "m", delta: "$42 on groceries." },
      { type: "TOOL_CALL_START", toolCallId: "tc", toolCallName: "summarize_spending" },
      { type: "TOOL_CALL_END", toolCallId: "tc" },
      { type: "CUSTOM", name: "token_usage", value: { inputTokens: 10, outputTokens: 20, totalTokens: 30 } },
      { type: "TEXT_MESSAGE_END", messageId: "m" },
      { type: "RUN_FINISHED", threadId: "t", runId: "r", result: { conversationId: "conv-9" } },
    ]);
    renderAgui(fetchMock);

    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "groceries?" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(screen.getByText("You spent $42 on groceries.")).toBeTruthy());
    expect(screen.getByText(/summarize_spending/)).toBeTruthy();

    // The turn went to the AG-UI endpoint (no hub involved).
    const call = fetchMock.mock.calls.find((c) => String(c[0]).includes("/api/agui/finance"));
    expect(call).toBeTruthy();

    // The next turn threads the SERVER conversation id, so the backend resumes it directly.
    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "and last month?" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    await waitFor(() => {
      const second = fetchMock.mock.calls.filter((c) => String(c[0]).includes("/api/agui/finance"))[1];
      expect(second).toBeTruthy();
      expect(JSON.parse((second![1] as RequestInit).body as string).threadId).toBe("conv-9");
    });
  });

  it("surfaces RUN_ERROR as a failed turn with retry", async () => {
    renderAgui(aguiAwareFetch([
      { type: "RUN_STARTED", threadId: "t", runId: "r" },
      { type: "RUN_ERROR", message: "The assistant could not complete the request." },
    ]));

    fireEvent.change(screen.getByLabelText("Message"), { target: { value: "hello" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(screen.getByText(/could not complete/)).toBeTruthy());
    expect(screen.getByRole("button", { name: "Retry" })).toBeTruthy();
  });

  it("attaches a dropped file via the upload endpoint", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes("/api/files") && init?.method === "POST") {
        return Promise.resolve(jsonResponse({
          id: "f-1", fileName: "brief.pdf", contentType: "application/pdf", sizeBytes: 5, createdAt: "2026-07-02",
        }));
      }
      return Promise.resolve(jsonResponse({ permissions: [] }));
    });
    renderAgui(fetchMock);

    const dropZone = screen.getByLabelText("Message").closest("div.relative") ?? document.body;
    const file = new File(["hello"], "brief.pdf", { type: "application/pdf" });
    fireEvent.drop(dropZone, { dataTransfer: { files: [file], types: ["Files"] } });

    // The dropped file uploads and appears as an attachment chip.
    await waitFor(() => expect(screen.getByText("brief.pdf")).toBeTruthy());
    expect(fetchMock.mock.calls.some(
      (c) => String(c[0]).includes("/api/files") && (c[1] as RequestInit | undefined)?.method === "POST",
    )).toBe(true);
  });
});
