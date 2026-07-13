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

const donutTab: ModuleTab = {
  id: "breakdown",
  label: "Spending by category",
  route: "/finance/breakdown",
  dataEndpoint: "/api/finance/spending/by-category",
  chart: { kind: "donut", xField: "category", yField: "spent", yLabel: "Spent" },
};

describe("GenericTab (donut chart tabs)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders one segment per label with value and share in the legend", async () => {
    renderTab(donutTab, [
      { category: "Groceries", spent: 600 },
      { category: "Dining", spent: 300 },
      { category: "Transit", spent: 100 },
    ]);

    expect(await screen.findByTestId("donut-slice-0")).toBeTruthy();
    expect(screen.getByTestId("donut-slice-2")).toBeTruthy();
    const legend = screen.getByTestId("chart-legend");
    expect(legend.textContent).toContain("Groceries");
    expect(legend.textContent).toContain("60%");
    expect(screen.getByTestId("donut-total").textContent).toBe("1,000");
  });

  it("sums rows sharing a label instead of drawing duplicate segments", async () => {
    renderTab(donutTab, [
      { category: "Groceries", spent: 400 },
      { category: "Groceries", spent: 200 },
      { category: "Dining", spent: 400 },
    ]);

    expect(await screen.findByTestId("donut-slice-0")).toBeTruthy();
    expect(screen.queryByTestId("donut-slice-2")).toBeNull();
    expect(screen.getByTestId("chart-legend").textContent).toContain("600");
  });

  it("rolls segments beyond the palette into Other", async () => {
    renderTab(donutTab, [
      { category: "A", spent: 500 },
      { category: "B", spent: 400 },
      { category: "C", spent: 300 },
      { category: "D", spent: 200 },
      { category: "E", spent: 60 },
      { category: "F", spent: 40 },
    ]);

    // 4 named + Other = 5 segments; E and F merge.
    expect(await screen.findByTestId("donut-slice-4")).toBeTruthy();
    expect(screen.queryByTestId("donut-slice-5")).toBeNull();
    const legend = screen.getByTestId("chart-legend");
    expect(legend.textContent).toContain("Other");
    expect(legend.textContent).toContain("100");
  });

  it("ignores non-positive and non-numeric values, showing the empty state when nothing survives", async () => {
    renderTab(donutTab, [
      { category: "Refund", spent: -50 },
      { category: "Pending", spent: "n/a" },
    ]);

    expect(await screen.findByText(/No data points yet/)).toBeTruthy();
  });
});

const cashFlowTab: ModuleTab = {
  id: "cash-flow",
  label: "Cash flow",
  route: "/finance/cash-flow",
  dataEndpoint: "/api/finance/cash-flow",
  chart: { kind: "bar", xField: "month", yField: "amount", seriesField: "direction", yLabel: "Amount" },
};

describe("GenericTab (bar chart tabs)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders one bar per series per category with a legend when there are two series", async () => {
    renderTab(cashFlowTab, [
      { month: "May", amount: 5000, direction: "Income" },
      { month: "May", amount: 3200, direction: "Expense" },
      { month: "June", amount: 5000, direction: "Income" },
      { month: "June", amount: 4100, direction: "Expense" },
    ]);

    expect(await screen.findByTestId("chart-bar-0-0")).toBeTruthy();
    expect(screen.getByTestId("chart-bar-1-1")).toBeTruthy();
    const legend = screen.getByTestId("chart-legend");
    expect(legend.textContent).toContain("Income");
    expect(legend.textContent).toContain("Expense");
  });

  it("shows no legend for a single series — the tab names it", async () => {
    renderTab(
      { ...cashFlowTab, chart: { kind: "bar", xField: "month", yField: "amount" } },
      [
        { month: "May", amount: 3200 },
        { month: "June", amount: 4100 },
      ],
    );

    expect(await screen.findByTestId("chart-bar-0-0")).toBeTruthy();
    expect(screen.queryByTestId("chart-legend")).toBeNull();
  });

  it("keeps bars anchored to a zero baseline so negatives hang below it", async () => {
    renderTab(
      { ...cashFlowTab, chart: { kind: "bar", xField: "month", yField: "amount" } },
      [
        { month: "May", amount: 400 },
        { month: "June", amount: -200 },
      ],
    );

    const positive = await screen.findByTestId("chart-bar-0-0");
    const negative = screen.getByTestId("chart-bar-0-1");
    // The positive bar ends where the negative bar begins: both touch the zero line.
    const positiveBottom = Number(positive.getAttribute("y")) + Number(positive.getAttribute("height"));
    expect(Math.abs(positiveBottom - Number(negative.getAttribute("y")))).toBeLessThan(1);
  });

  it("skips rows whose value does not parse rather than drawing garbage", async () => {
    renderTab(
      { ...cashFlowTab, chart: { kind: "bar", xField: "month", yField: "amount" } },
      [
        { month: "May", amount: 400 },
        { month: "June", amount: "nope" },
      ],
    );

    expect(await screen.findByTestId("chart-bar-0-0")).toBeTruthy();
    expect(screen.queryByTestId("chart-bar-0-1")).toBeNull();
  });

  it("renders an honest empty state instead of an empty plot", async () => {
    renderTab(cashFlowTab, []);

    expect(await screen.findByText(/No data points yet/)).toBeTruthy();
  });
});
