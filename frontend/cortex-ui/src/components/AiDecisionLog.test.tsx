// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AiDecisionLog } from "./AiDecisionLog";
import type { AiDecision } from "../lib/api";

const base: AiDecision = {
  id: "d1",
  source: "approval",
  occurredAt: "2026-07-10T09:30:00Z",
  moduleId: "finance",
  moduleName: "Finance",
  toolName: "update_budget",
  toolDescription: "Updates a budget's monthly limit.",
  summary: "Updates a budget's monthly limit. — name: Groceries, amount: 400",
  basis: "the user asked to raise the grocery budget",
  oversight: "approved",
  risk: "high",
  requestedBy: "Ada",
  decidedBy: "Dana Reviewer",
  decidedAt: "2026-07-10T09:31:00Z",
  conversationId: "c1",
  error: null,
};

function renderLog(decisions: AiDecision[]) {
  const fetchMock = vi.fn(() =>
    Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(decisions),
    } as unknown as Response),
  );
  vi.stubGlobal("fetch", fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <AiDecisionLog />
    </QueryClientProvider>,
  );
  return fetchMock;
}

describe("AiDecisionLog (the account-level ADMT disclosure view)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("shows an empty state before any AI-originated action exists", async () => {
    renderLog([]);
    expect(
      await screen.findByText(/No AI-originated actions have been recorded yet/),
    ).toBeTruthy();
  });

  it("renders each decision with its plain-language summary and basis", async () => {
    renderLog([base]);

    expect(await screen.findByText("Updates a budget's monthly limit.")).toBeTruthy();
    expect(screen.getByText(/name: Groceries, amount: 400/)).toBeTruthy();
    expect(screen.getByText(/Why: the user asked to raise the grocery budget/)).toBeTruthy();
  });

  it("an approved decision is badged with who approved it — icon and text, never color alone", async () => {
    renderLog([base]);

    const badge = (await screen.findByText("Approved by Dana Reviewer")).closest("span")!;
    expect(badge.querySelector("svg")).toBeTruthy();
    expect(badge.className).toContain("emerald");
  });

  it("a rejected decision is badged as the human no", async () => {
    renderLog([{ ...base, id: "d2", oversight: "rejected", decidedBy: "Rex" }]);

    const badge = (await screen.findByText("Rejected by Rex")).closest("span")!;
    expect(badge.querySelector("svg")).toBeTruthy();
    expect(badge.className).toContain("red");
  });

  it("an ungated execution is badged Automatic with an explanation", async () => {
    renderLog([
      {
        ...base,
        id: "d3",
        source: "audit",
        oversight: "automatic",
        risk: null,
        decidedBy: null,
        decidedAt: null,
      },
    ]);

    const badge = (await screen.findByText("Automatic")).closest("span")!;
    expect(badge.querySelector("svg")).toBeTruthy();
    expect(badge.getAttribute("title")).toMatch(/not approval-gated/);
  });

  it("an approved-but-failed execution still discloses the failure", async () => {
    renderLog([{ ...base, id: "d4", error: "provider timeout" }]);

    expect(await screen.findByText(/Did not complete: provider timeout/)).toBeTruthy();
  });

  it("groups entries under one heading per day, recent-first order preserved", async () => {
    renderLog([
      // a and b share an instant (same local day in EVERY timezone); c is a month earlier.
      { ...base, id: "a", occurredAt: "2026-07-10T12:00:00Z" },
      { ...base, id: "b", occurredAt: "2026-07-10T12:00:00Z" },
      { ...base, id: "c", occurredAt: "2026-06-01T12:00:00Z" },
    ]);

    expect(await screen.findAllByTestId("ai-decision-entry")).toHaveLength(3);
    // Two distinct days → exactly two day headings (same-day entries share one).
    expect(screen.getAllByRole("heading", { level: 3 })).toHaveLength(2);
  });

  it("offers a Download export of the disclosure, disabled when there is nothing to export", async () => {
    renderLog([]);
    const button = await screen.findByRole("button", { name: "Download" });
    expect((button as HTMLButtonElement).disabled).toBe(true);
  });

  it("Download builds a JSON file from exactly the fetched records", async () => {
    const created: Blob[] = [];
    vi.stubGlobal("URL", {
      ...URL,
      createObjectURL: vi.fn((blob: Blob) => {
        created.push(blob);
        return "blob:disclosure";
      }),
      revokeObjectURL: vi.fn(),
    });
    // jsdom can't navigate/download — swallow the anchor click and assert it fired.
    const anchorClick = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(() => {});
    renderLog([base]);

    fireEvent.click(await screen.findByRole("button", { name: "Download" }));

    expect(anchorClick).toHaveBeenCalledTimes(1);
    expect(created).toHaveLength(1);
    const payload = JSON.parse(await created[0].text()) as {
      disclosure: string;
      count: number;
      decisions: AiDecision[];
    };
    expect(payload.disclosure).toBe("ai-decision-history");
    expect(payload.count).toBe(1);
    expect(payload.decisions[0].id).toBe("d1");
    expect(payload.decisions[0].decidedBy).toBe("Dana Reviewer");
  });
});
