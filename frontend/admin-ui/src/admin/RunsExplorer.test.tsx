// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RunsExplorer } from "./RunsExplorer";

const completed = {
  id: "11111111-1111-1111-1111-111111111111",
  occurredAt: "2026-08-05T20:55:04Z",
  userDisplay: "Dev User",
  moduleId: "nutrition",
  conversationId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  provider: "Mock",
  model: "gpt-4o-mini",
  instructionsHash: "a5b8cd98f59cbeca",
  outcome: "Completed",
  firstTokenMs: 33,
  totalMs: 990,
  toolCallCount: 1,
  approvalCount: 0,
  inputTokens: 12,
  outputTokens: 98,
  totalTokens: 110,
  traceId: "1b7687b879376df264a3eb0ebf482cda",
};

// The case the whole feature exists for: refused before the model, so it cost nothing.
const refused = {
  id: "22222222-2222-2222-2222-222222222222",
  occurredAt: "2026-08-05T20:54:29Z",
  userDisplay: "Dev User",
  moduleId: "finance",
  outcome: "BudgetExceeded",
  errorKind: "ConversationBudget",
  errorMessage: "Conversation consumed 1,200 of 1,000 tokens.",
  totalMs: 57,
  toolCallCount: 0,
  approvalCount: 0,
  inputTokens: 0,
  outputTokens: 0,
  totalTokens: 0,
};

const list = {
  // p95 deliberately differs from any row's latency so the tile and the table can be told apart.
  summary: { total: 2, errors: 1, errorRate: 0.5, p50Ms: 57, p95Ms: 1200, totalTokens: 110, cost: 0 },
  runs: [refused, completed],
  modules: ["finance", "nutrition"],
  models: ["gpt-4o-mini"],
  outcomes: ["Completed", "Error", "BudgetExceeded", "ModuleUnavailable", "Cancelled"],
};

const detail = { run: completed, toolCalls: [], steps: [] };

function renderRuns(listData: unknown = list, detailData: unknown = detail) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    void init;
    const url = String(input);
    // /runs/{id} is the detail; /runs?... is the list.
    const isDetail = /\/runs\/[0-9a-f-]+/.test(url);
    return Promise.resolve({
      ok: true,
      json: () => Promise.resolve(isDetail ? detailData : listData),
    } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RunsExplorer />
    </QueryClientProvider>,
  );
  return fetchMock;
}

/** The outcome names also appear as filter <option>s, so table assertions scope to the table. */
async function table() {
  return await screen.findByRole("table");
}

describe("RunsExplorer (every turn, however it ended)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the window's health and both a completed and a refused turn", async () => {
    renderRuns();

    const rows = within(await table());
    expect(rows.getByText("Completed")).toBeTruthy();
    expect(rows.getByText("BudgetExceeded")).toBeTruthy();
    expect(rows.getByText("990 ms")).toBeTruthy();
    // Summary tiles cover the whole window, not just the page of rows below them.
    expect(screen.getByText("50.0%")).toBeTruthy();
    expect(screen.getByText("1.20 s")).toBeTruthy();
  });

  it("shows the failure cause beside the outcome rather than hiding it in a tooltip", async () => {
    renderRuns();

    // Visible in the list, and it does not replace the badge's accessible name.
    expect(within(await table()).getByText("ConversationBudget")).toBeTruthy();
  });

  it("renders a turn that produced no tokens as a dash, not a zero", async () => {
    renderRuns();

    // A refused turn has no usage at all — "0" would imply a model ran and returned nothing.
    expect(within(await table()).getAllByText("—").length).toBeGreaterThan(0);
  });

  it("drills into a run and back", async () => {
    const fetchMock = renderRuns();

    fireEvent.click(within(await table()).getByText("nutrition"));

    await waitFor(() =>
      expect(fetchMock.mock.calls.some((c) => /\/runs\/[0-9a-f-]+/.test(String(c[0])))).toBe(true),
    );
    expect(await screen.findByText("First token")).toBeTruthy();
    expect(screen.getByText("This turn invoked no tools.")).toBeTruthy();

    fireEvent.click(screen.getByText("← Back to runs"));
    expect(await screen.findByText("Agent Runs")).toBeTruthy();
  });

  it("refetches when the outcome filter changes", async () => {
    const fetchMock = renderRuns();
    await table();

    fireEvent.change(screen.getByLabelText("Outcome"), { target: { value: "BudgetExceeded" } });

    await waitFor(() =>
      expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("outcome=BudgetExceeded"))).toBe(true),
    );
  });
});
