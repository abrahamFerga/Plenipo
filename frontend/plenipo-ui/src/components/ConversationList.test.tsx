// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ConversationList } from "./ConversationList";

const CONVERSATIONS = [
  { id: "c1", moduleId: "finance", title: "Summarize my spending", updatedAt: "2026-06-28T10:00:00Z" },
  { id: "c2", moduleId: "finance", title: "Budget check", updatedAt: "2026-06-27T10:00:00Z" },
];

function stubFetch() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/chat/conversations") && method === "GET") {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(CONVERSATIONS) } as unknown as Response);
    }
    // DELETE (and any other call) succeeds with no body.
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderList(props: Partial<Parameters<typeof ConversationList>[0]> = {}) {
  const onSelect = vi.fn();
  const onNew = vi.fn();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <ConversationList moduleId="finance" onSelect={onSelect} onNew={onNew} {...props} />
    </QueryClientProvider>,
  );
  return { onSelect, onNew };
}

describe("ConversationList", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("lists the user's conversations and selects one on click", async () => {
    stubFetch();
    const { onSelect } = renderList();

    fireEvent.click(await screen.findByText("Summarize my spending"));
    expect(onSelect).toHaveBeenCalledWith("c1");
  });

  it("starts a new chat", async () => {
    stubFetch();
    const { onNew } = renderList();

    await screen.findByText("Summarize my spending"); // wait for the list to load
    fireEvent.click(screen.getByRole("button", { name: "+ New chat" }));
    expect(onNew).toHaveBeenCalled();
  });

  it("deletes the active conversation after confirmation and resets the selection", async () => {
    const fetchMock = stubFetch();
    const { onNew } = renderList({ selectedId: "c1" });

    await screen.findByText("Summarize my spending");
    fireEvent.click(screen.getAllByRole("button", { name: "Delete conversation" })[0]);

    // The confirmation dialog opens; confirming issues the delete.
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete" }));

    // The owner-scoped DELETE is issued for the selected conversation…
    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          (c) => String(c[0]).includes("/api/chat/conversations/c1") && (c[1] as RequestInit | undefined)?.method === "DELETE",
        ),
      ).toBe(true),
    );
    // …and because it was the active one, the view falls back to a fresh chat.
    await waitFor(() => expect(onNew).toHaveBeenCalled());
  });

  it("renames a conversation via PUT using the prompted title", async () => {
    const fetchMock = stubFetch();
    vi.spyOn(window, "prompt").mockReturnValue("Renamed chat");
    renderList();

    await screen.findByText("Summarize my spending");
    fireEvent.click(screen.getAllByRole("button", { name: "Rename conversation" })[0]);

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) => String(c[0]).includes("/api/chat/conversations/c1/title") && (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      expect(put).toBeTruthy();
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({ title: "Renamed chat" });
    });
  });

  it("does not delete when the confirmation is dismissed", async () => {
    const fetchMock = stubFetch();
    renderList({ selectedId: "c1" });

    await screen.findByText("Summarize my spending");
    fireEvent.click(screen.getAllByRole("button", { name: "Delete conversation" })[0]);

    // Cancelling the dialog closes it and sends no DELETE.
    const dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(
      fetchMock.mock.calls.some((c) => (c[1] as RequestInit | undefined)?.method === "DELETE"),
    ).toBe(false);
  });
});
