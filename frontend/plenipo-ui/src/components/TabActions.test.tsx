// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GenericTab } from "./GenericTab";
import type { ModuleTab } from "../lib/api";

const reviewTab: ModuleTab = {
  id: "review",
  label: "Statement review",
  route: "/finance/review",
  dataEndpoint: "/api/finance/imports/latest/lines",
  columns: [{ field: "description", header: "Description" }],
  actions: [
    {
      id: "approve",
      label: "Approve batch",
      endpoint: "/api/finance/imports/latest/approve",
      confirm: "Post every line above as transactions?",
    },
  ],
};

function renderTab(tab: ModuleTab, fetchImpl: (url: string, init?: RequestInit) => Promise<unknown>) {
  vi.stubGlobal("fetch", vi.fn(fetchImpl));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <GenericTab tab={tab} />
    </QueryClientProvider>,
  );
}

const okJson = (body: unknown) =>
  Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as unknown as Response);

describe("GenericTab (tab actions)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("POSTs after confirmation and surfaces the endpoint's message", async () => {
    const calls: string[] = [];
    renderTab(reviewTab, (url, init) => {
      calls.push(`${init?.method ?? "GET"} ${url}`);
      if (init?.method === "POST") return okJson({ message: "Posted 3 transaction(s)." });
      return okJson([{ description: "WHOLE FOODS" }]);
    });

    fireEvent.click(await screen.findByRole("button", { name: "Approve batch" }));
    // The confirm dialog interposes for consequential actions.
    expect(screen.getByText("Post every line above as transactions?")).toBeTruthy();
    const confirmButtons = screen.getAllByRole("button", { name: "Approve batch" });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);

    expect((await screen.findByTestId("action-message")).textContent).toContain("Posted 3 transaction(s).");
    expect(calls.some((c) => c.includes("POST") && c.includes("/imports/latest/approve"))).toBe(true);
  });

  it("shows the server's error instead of pretending success", async () => {
    renderTab(reviewTab, (_url, init) => {
      if (init?.method === "POST") {
        return Promise.resolve({
          ok: false,
          status: 409,
          statusText: "Conflict",
          text: () => Promise.resolve('{"message":"Only a parsed batch can be approved."}'),
        } as unknown as Response);
      }
      return okJson([]);
    });

    fireEvent.click(await screen.findByRole("button", { name: "Approve batch" }));
    const confirmButtons = screen.getAllByRole("button", { name: "Approve batch" });
    fireEvent.click(confirmButtons[confirmButtons.length - 1]);

    expect((await screen.findByTestId("action-message")).textContent).toContain("Only a parsed batch can be approved.");
  });
});
