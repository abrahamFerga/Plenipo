// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";

// Replace the SignalR connection with a stub so the panel renders without a real hub. Stream
// subscribers are captured so tests can push events (tokens, errors) into the panel.
const { mockConnection, disposeMock, streamObservers } = vi.hoisted(() => {
  const disposeMock = vi.fn();
  const streamObservers: Array<{
    next: (event: unknown) => void;
    complete: () => void;
    error: (e: unknown) => void;
  }> = [];
  return {
    disposeMock,
    streamObservers,
    mockConnection: {
      start: vi.fn(() => Promise.resolve()),
      stop: vi.fn(() => Promise.resolve()),
      stream: vi.fn(() => ({
        subscribe: vi.fn((observer: (typeof streamObservers)[number]) => {
          streamObservers.push(observer);
          return { dispose: disposeMock };
        }),
      })),
    },
  };
});

vi.mock("../lib/signalr", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../lib/signalr")>();
  return { ...actual, createAgentConnection: (): HubConnection => mockConnection as unknown as HubConnection };
});

import { ChatPanel } from "./ChatPanel";

function renderChat() {
  // /me returns no approval rights, so the embedded PendingApprovals stays hidden.
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.resolve({ ok: true, json: () => Promise.resolve({ permissions: [] }) } as unknown as Response)),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      {/* These tests exercise the SignalR transport explicitly; AG-UI has its own spec file. */}
      <ChatPanel moduleId="finance" transport="signalr" suggestedPrompts={["Summarize my spending"]} />
    </QueryClientProvider>,
  );
}

describe("ChatPanel", () => {
  beforeAll(() => {
    // jsdom doesn't implement Element.scrollTo, which the chat list's auto-scroll effect calls.
    Element.prototype.scrollTo = vi.fn() as unknown as typeof Element.prototype.scrollTo;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
    streamObservers.length = 0;
  });

  it("offers the starter prompts and streams the one the user clicks", () => {
    renderChat();

    fireEvent.click(screen.getByRole("button", { name: "Summarize my spending" }));

    // Clicking a starter sends it straight to the hub's streaming method.
    expect(mockConnection.stream).toHaveBeenCalledWith(
      "Stream",
      expect.objectContaining({ moduleId: "finance", message: "Summarize my spending" }),
    );
  });

  it("gives the message input an accessible label (its placeholder vanishes once you type)", () => {
    renderChat();
    expect(screen.getByRole("textbox", { name: "Message" })).toBeTruthy();
  });

  it("sends the typed message on Enter", () => {
    renderChat();

    const box = screen.getByRole("textbox", { name: "Message" });
    fireEvent.change(box, { target: { value: "What did I spend?" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(mockConnection.stream).toHaveBeenCalledWith(
      "Stream",
      expect.objectContaining({ moduleId: "finance", message: "What did I spend?" }),
    );
  });

  it("does not send on Shift+Enter (that inserts a newline instead)", () => {
    renderChat();

    const box = screen.getByRole("textbox", { name: "Message" });
    fireEvent.change(box, { target: { value: "first line" } });
    fireEvent.keyDown(box, { key: "Enter", shiftKey: true });

    expect(mockConnection.stream).not.toHaveBeenCalled();
  });

  it("on a stream failure keeps the user turn, drops the empty assistant bubble, and Retry resends the same text", () => {
    renderChat();

    const box = screen.getByRole("textbox", { name: "Message" });
    fireEvent.change(box, { target: { value: "Summarize the brief" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    // While no tokens have arrived, the assistant bubble shows the typing indicator.
    expect(mockConnection.stream).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("status", { name: "Assistant is responding" })).toBeTruthy();

    // The hub stream errors before the assistant produced anything.
    act(() => {
      streamObservers[0].error(new Error("hub went away"));
    });

    // The user's message survives; the dead assistant placeholder is removed.
    expect(screen.getByText("Summarize the brief")).toBeTruthy();
    expect(screen.queryByRole("status", { name: "Assistant is responding" })).toBeNull();

    // Retry clears the error and issues a second stream call with the identical text.
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(mockConnection.stream).toHaveBeenCalledTimes(2);
    expect(mockConnection.stream).toHaveBeenLastCalledWith(
      "Stream",
      expect.objectContaining({ moduleId: "finance", message: "Summarize the brief" }),
    );

    // The retried user turn is not duplicated in the transcript.
    expect(screen.getAllByText("Summarize the brief")).toHaveLength(1);
  });

  it("renders a message's [Attached files] block as download chips instead of raw text", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/api/chat/conversations/c2/messages")) {
          return Promise.resolve({
            ok: true,
            json: () =>
              Promise.resolve([
                {
                  id: "m1",
                  role: "User",
                  content: "Please review\n\n[Attached files]\n- brief.pdf (file id: f-123)",
                },
              ]),
          } as unknown as Response);
        }
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ permissions: [] }) } as unknown as Response);
      }),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ChatPanel moduleId="legal" transport="signalr" conversationId="c2" />
      </QueryClientProvider>,
    );

    // The file reference renders as a download chip linking to the file endpoint...
    const chip = await screen.findByRole("link", { name: "brief.pdf" });
    expect(chip.getAttribute("href")).toContain("/api/files/f-123");
    // ...and the body shows without the raw block.
    expect(screen.getByText("Please review")).toBeTruthy();
  });

  it("shows a Stop button while streaming and cancels the turn (disposing the stream) when clicked", () => {
    renderChat();

    // Sending a turn puts the panel into the streaming state → the Send button becomes Stop.
    fireEvent.click(screen.getByRole("button", { name: "Summarize my spending" }));
    fireEvent.click(screen.getByRole("button", { name: "Stop" }));

    // Disposing the subscription cancels the server-side run; the panel returns to idle (Send is back).
    expect(disposeMock).toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Send" })).toBeTruthy();
  });

  it("uploads an attachment, shows its chip, and sends the message with the file reference", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.includes("/api/files") && init?.method === "POST") {
          return Promise.resolve({
            ok: true,
            json: () =>
              Promise.resolve({ id: "f-123", fileName: "brief.pdf", contentType: "application/pdf", sizeBytes: 2048 }),
          } as unknown as Response);
        }
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ permissions: [] }) } as unknown as Response);
      }),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ChatPanel moduleId="legal" transport="signalr" />
      </QueryClientProvider>,
    );

    // Pick a file through the (hidden) input behind the paperclip button.
    const file = new File(["%PDF-1.7 fake"], "brief.pdf", { type: "application/pdf" });
    fireEvent.change(screen.getByLabelText("Attach file"), { target: { files: [file] } });

    // The chip appears once the upload resolves.
    expect(await screen.findByText("brief.pdf")).toBeTruthy();

    // Sending includes the typed text plus the attachment reference the document tools consume.
    fireEvent.change(screen.getByRole("textbox", { name: "Message" }), {
      target: { value: "Store this as part of the case of Julia Assange" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect(mockConnection.stream).toHaveBeenCalledWith(
      "Stream",
      expect.objectContaining({
        moduleId: "legal",
        message: expect.stringContaining("file id: f-123"),
      }),
    );

    // The chip row clears after sending.
    expect(screen.queryByLabelText("Attachments")).toBeNull();
  });

  it("refuses an attachment over the server's published limit without uploading it", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes("/api/files") && init?.method === "POST") {
        throw new Error("an oversized file must never be uploaded");
      }
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ permissions: [] }) } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    // Seed the deployment facts (staleTime: Infinity) so the preflight limit is present up front.
    client.setQueryData(["info"], { chatEnabled: true, demoMode: true, maxUploadBytes: 10 });
    render(
      <QueryClientProvider client={client}>
        <ChatPanel moduleId="legal" transport="signalr" />
      </QueryClientProvider>,
    );

    const big = new File(["this content is larger than ten bytes"], "huge.pdf", { type: "application/pdf" });
    fireEvent.change(screen.getByLabelText("Attach file"), { target: { files: [big] } });

    // Friendly refusal names the file and the limit; no chip, no POST.
    expect(await screen.findByText(/"huge\.pdf" .* exceeds the 10 B upload limit/)).toBeTruthy();
    expect(screen.queryByText("huge.pdf")).toBeNull();
    expect(
      fetchMock.mock.calls.some(
        (c) => String(c[0]).includes("/api/files") && (c[1] as RequestInit | undefined)?.method === "POST",
      ),
    ).toBe(false);
  });

  it("resumes a conversation by loading and rendering its message history", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/api/chat/conversations/c1/messages")) {
          return Promise.resolve({
            ok: true,
            json: () =>
              Promise.resolve([
                { id: "m1", role: "User", content: "What did I spend?" },
                { id: "m2", role: "Assistant", content: "You spent 1,200 on groceries." },
              ]),
          } as unknown as Response);
        }
        // /me and anything else: a user with no special permissions.
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ permissions: [] }) } as unknown as Response);
      }),
    );

    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <ChatPanel moduleId="finance" transport="signalr" conversationId="c1" />
      </QueryClientProvider>,
    );

    // Selecting a conversation loads its persisted history — both the user turn and the assistant reply.
    expect(await screen.findByText("What did I spend?")).toBeTruthy();
    expect(screen.getByText("You spent 1,200 on groceries.")).toBeTruthy();
  });
});
