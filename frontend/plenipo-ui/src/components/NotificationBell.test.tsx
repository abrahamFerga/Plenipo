// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { NotificationBell } from "./NotificationBell";

function stubApi(items: unknown[]) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.includes("/api/notifications/") && (!init?.method || init.method === "GET")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(items) } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderBell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <NotificationBell />
    </QueryClientProvider>,
  );
}

const unreadItem = {
  id: "n1",
  category: "jobs",
  title: "Job finished: legal.bulk-review",
  body: "All done.",
  createdAt: new Date().toISOString(),
};

describe("NotificationBell (in-app inbox)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the unread count on the bell and lists items when opened", async () => {
    stubApi([unreadItem, { ...unreadItem, id: "n2", readAt: new Date().toISOString() }]);
    renderBell();

    // Badge counts only unread (1 of the 2 items).
    const bell = await screen.findByRole("button", { name: "Notifications (1 unread)" });
    fireEvent.click(bell);

    expect(await screen.findByRole("dialog", { name: "Notifications" })).toBeTruthy();
    expect(screen.getAllByText("Job finished: legal.bulk-review").length).toBe(2);
    expect(screen.getByRole("button", { name: "Mark all read" })).toBeTruthy();
  });

  it("marks an item read via POST and refreshes the list", async () => {
    const fetchMock = stubApi([unreadItem]);
    renderBell();

    fireEvent.click(await screen.findByRole("button", { name: "Notifications (1 unread)" }));
    fireEvent.click(await screen.findByRole("button", { name: 'Mark "Job finished: legal.bulk-review" read' }));

    await waitFor(() => {
      const posted = fetchMock.mock.calls.some(
        ([input, init]) =>
          String(input).endsWith("/api/notifications/n1/read") && (init as RequestInit)?.method === "POST",
      );
      expect(posted).toBe(true);
    });
  });

  it("shows a friendly empty state", async () => {
    stubApi([]);
    renderBell();

    fireEvent.click(await screen.findByRole("button", { name: "Notifications" }));
    expect(await screen.findByText(/Nothing yet/)).toBeTruthy();
  });
});
