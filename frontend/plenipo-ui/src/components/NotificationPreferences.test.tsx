// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { NotificationPreferences } from "./NotificationPreferences";

const PREFS = [
  { id: "bill-reminders", label: "Bill reminders", description: "A recurring charge is due soon.", moduleId: "finance", enabled: true },
  { id: "imports", label: "Statement imports", description: null, moduleId: "finance", enabled: false },
];

function stubApi() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/notifications/preferences") && method === "GET") {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(PREFS) } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderPrefs() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <NotificationPreferences />
    </QueryClientProvider>,
  );
}

describe("NotificationPreferences", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("shows each declared category with its current stance", async () => {
    stubApi();
    renderPrefs();

    expect(await screen.findByText("Bill reminders")).toBeTruthy();
    const bill = screen.getByLabelText("Bill reminders notifications") as HTMLInputElement;
    const imports = screen.getByLabelText("Statement imports notifications") as HTMLInputElement;
    expect(bill.checked).toBe(true);
    expect(imports.checked).toBe(false);
  });

  it("mutes a category with a PUT to its preference", async () => {
    const fetchMock = stubApi();
    renderPrefs();
    await screen.findByText("Bill reminders");

    fireEvent.click(screen.getByLabelText("Bill reminders notifications"));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) =>
          String(c[0]).includes("/api/notifications/preferences/bill-reminders") &&
          (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      expect(put).toBeTruthy();
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({ enabled: false });
    });
  });
});
