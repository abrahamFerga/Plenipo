// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GenericTab } from "./GenericTab";
import type { ModuleTab } from "../lib/api";

/**
 * The narrow-viewport card mode. jsdom has no real matchMedia, so these tests stub one that
 * matches — the sibling GenericTab suite runs without a stub and so keeps pinning the table.
 */

function stubNarrowViewport() {
  vi.stubGlobal(
    "matchMedia",
    vi.fn().mockReturnValue({
      matches: true,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    } as unknown as MediaQueryList),
  );
}

const accountsTab: ModuleTab = {
  id: "accounts",
  label: "Accounts",
  route: "/finance/accounts",
  dataEndpoint: "/api/finance/accounts",
  columns: [
    { field: "name", header: "Account" },
    { field: "type", header: "Type" },
    { field: "balance", header: "Balance" },
    { field: "currency", header: "Currency" },
    { field: "institution", header: "Institution" },
  ],
};

function renderTab(tab: ModuleTab, rows: unknown) {
  stubNarrowViewport();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(rows) } as unknown as Response),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <GenericTab tab={tab} />
    </QueryClientProvider>,
  );
}

describe("GenericTab (narrow-viewport card mode)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders cards instead of a table: first column as title, next two visible", async () => {
    renderTab(accountsTab, [{ name: "Everyday checking", type: "checking", balance: 1200, currency: "USD", institution: "Acme Bank" }]);

    const card = await screen.findByTestId("row-card");
    expect(screen.queryByRole("table")).toBeNull();
    expect(card.textContent).toContain("Everyday checking");
    expect(card.textContent).toContain("checking");
    expect(card.textContent).toContain("1200");
  });

  it("tucks columns beyond the first three behind a More disclosure", async () => {
    renderTab(accountsTab, [{ name: "Everyday checking", type: "checking", balance: 1200, currency: "USD", institution: "Acme Bank" }]);

    const card = await screen.findByTestId("row-card");
    const details = card.querySelector("details")!;
    expect(details).toBeTruthy();
    expect(details.open).toBe(false);
    expect(details.textContent).toContain("Currency");
    expect(details.textContent).toContain("Acme Bank");
  });

  it("renders no disclosure when every column fits on the card", async () => {
    renderTab(
      { ...accountsTab, columns: accountsTab.columns!.slice(0, 3) },
      [{ name: "Everyday checking", type: "checking", balance: 1200 }],
    );

    const card = await screen.findByTestId("row-card");
    expect(card.querySelector("details")).toBeNull();
  });

  it("keeps the empty state honest in card mode", async () => {
    renderTab(accountsTab, []);

    expect(await screen.findByText("No data yet.")).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("cards carry the same row affordances as table rows — Delete confirms then DELETEs", async () => {
    stubNarrowViewport();
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      void init;
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve([{ slug: "confidentiality", title: "Confidentiality" }]),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab
          tab={{
            id: "clauses",
            label: "Clauses",
            route: "/legal/clauses",
            dataEndpoint: "/api/legal/clauses",
            columns: [{ field: "title", header: "Clause" }],
            editor: {
              upsertEndpoint: "/api/legal/clauses",
              deleteEndpoint: "/api/legal/clauses/{slug}",
              keyField: "slug",
              fields: [
                { field: "slug", label: "Type" },
                { field: "title", label: "Title" },
              ],
            },
          }}
        />
      </QueryClientProvider>,
    );

    await screen.findByTestId("row-card");
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    const deletes = screen.getAllByRole("button", { name: "Delete" });
    fireEvent.click(deletes[deletes.length - 1]);

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(
          (c) =>
            String(c[0]).endsWith("/api/legal/clauses/confidentiality") &&
            (c[1] as RequestInit | undefined)?.method === "DELETE",
        ),
      ).toBe(true);
    });
  });
});
