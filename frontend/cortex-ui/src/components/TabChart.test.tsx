// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GenericTab } from "./GenericTab";
import type { ModuleTab } from "../lib/api";

const trendTab: ModuleTab = {
  id: "trend",
  label: "Net worth",
  route: "/finance/trend",
  dataEndpoint: "/api/finance/net-worth/history",
  chart: { xField: "takenOn", yField: "netWorth", seriesField: "currencyCode", yLabel: "Net worth" },
};

function renderTab(tab: ModuleTab, rows: unknown) {
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

describe("GenericTab (chart tabs)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders one line per series with a legend when there are two", async () => {
    renderTab(trendTab, [
      { takenOn: "2026-07-01", netWorth: 1000, currencyCode: "USD" },
      { takenOn: "2026-07-02", netWorth: 1100, currencyCode: "USD" },
      { takenOn: "2026-07-01", netWorth: 500, currencyCode: "EUR" },
      { takenOn: "2026-07-02", netWorth: 450, currencyCode: "EUR" },
    ]);

    expect(await screen.findByTestId("chart-line-0")).toBeTruthy();
    expect(screen.getByTestId("chart-line-1")).toBeTruthy();
    const legend = screen.getByTestId("chart-legend");
    expect(legend.textContent).toContain("USD");
    expect(legend.textContent).toContain("EUR");
  });

  it("shows no legend box for a single series — the tab names it", async () => {
    renderTab(trendTab, [
      { takenOn: "2026-07-01", netWorth: 1000, currencyCode: "USD" },
      { takenOn: "2026-07-02", netWorth: 1100, currencyCode: "USD" },
    ]);

    expect(await screen.findByTestId("chart-line-0")).toBeTruthy();
    expect(screen.queryByTestId("chart-legend")).toBeNull();
  });

  it("renders an honest empty state instead of an empty plot", async () => {
    renderTab(trendTab, []);

    expect(await screen.findByText(/No data points yet/)).toBeTruthy();
  });

  it("skips rows whose x or y does not parse rather than drawing garbage", async () => {
    renderTab(trendTab, [
      { takenOn: "2026-07-01", netWorth: 1000, currencyCode: "USD" },
      { takenOn: "not-a-date", netWorth: 1100, currencyCode: "USD" },
      { takenOn: "2026-07-03", netWorth: "nope", currencyCode: "USD" },
      { takenOn: "2026-07-04", netWorth: 1200, currencyCode: "USD" },
    ]);

    const line = await screen.findByTestId("chart-line-0");
    // Two valid points → a path with exactly one M and one L command.
    expect(line.getAttribute("d")?.match(/[ML]/g)?.length).toBe(2);
  });
});
