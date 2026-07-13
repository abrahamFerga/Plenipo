// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { PendingApprovals } from "./PendingApprovals";

// Route the API calls PendingApprovals makes: GET /me (to learn the user can approve), GET the pending
// list, and the POST /approve when clicked.
function stubApi() {
  const fetchMock = vi.fn((input: RequestInfo | URL) => {
    const url = String(input);
    if (url.endsWith("/api/platform/me")) {
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve({ userId: "u1", displayName: "Dev", tenantId: "t1", permissions: ["chat.approvals.manage"] }),
      } as unknown as Response);
    }
    if (url.endsWith("/api/chat/approvals")) {
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve([
            {
              id: "ap1",
              conversationId: "c1",
              moduleId: "finance",
              toolName: "record_transaction",
              argumentsJson: '{"description":"Lunch","amount":12}',
              createdAt: "2026-06-28",
            },
          ]),
      } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderApprovals() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PendingApprovals moduleId="finance" />
    </QueryClientProvider>,
  );
}

describe("PendingApprovals (human-in-the-loop gate)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("lists a blocked tool call with its arguments and approves it on click", async () => {
    const fetchMock = stubApi();
    renderApprovals();

    // The blocked side-effecting call surfaces, with its recorded arguments.
    expect(await screen.findByText("record_transaction")).toBeTruthy();
    expect(screen.getByText(/Lunch/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    // Approving re-executes that exact call on the server (POST …/approvals/ap1/approve).
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("/api/chat/approvals/ap1/approve"))).toBe(true),
    );
  });

  function stubApiWith(items: unknown[]) {
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith("/api/platform/me")) {
        return Promise.resolve({
          ok: true,
          json: () =>
            Promise.resolve({ userId: "u1", displayName: "Dev", tenantId: "t1", permissions: ["chat.approvals.manage"] }),
        } as unknown as Response);
      }
      if (url.endsWith("/api/chat/approvals")) {
        return Promise.resolve({ ok: true, json: () => Promise.resolve(items) } as unknown as Response);
      }
      return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    return fetchMock;
  }

  it("low-risk calls collapse to a compact one-tap confirm row", async () => {
    const fetchMock = stubApiWith([
      {
        id: "ap2",
        conversationId: "c1",
        moduleId: "finance",
        toolName: "categorize_transaction",
        description: "Categorize a transaction",
        argumentsJson: '{"transactionId":"t-9","category":"Groceries"}',
        createdAt: "2026-07-11",
        risk: "low",
      },
    ]);
    renderApprovals();

    const row = await screen.findByTestId("approval-compact");
    expect(screen.queryByTestId("approval-card")).toBeNull();
    expect(row.textContent).toContain("Categorize a transaction");
    // One tap: no confirm dialog in between.
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((c) => String(c[0]).includes("/api/chat/approvals/ap2/approve"))).toBe(true),
    );
  });

  it("high-risk calls keep the full card, with the agent's stated reasoning", async () => {
    stubApiWith([
      {
        id: "ap3",
        conversationId: "c1",
        moduleId: "finance",
        toolName: "set_budget",
        argumentsJson: '{"category":"Dining","amount":400,"reasoning":"Average of the last 3 months was $385."}',
        createdAt: "2026-07-11",
        risk: "high",
      },
    ]);
    renderApprovals();

    expect(await screen.findByTestId("approval-card")).toBeTruthy();
    expect(screen.queryByTestId("approval-compact")).toBeNull();
    expect(screen.getByTestId("approval-reasoning").textContent).toContain("Average of the last 3 months");
    // The reasoning is not repeated in the plain-argument line.
    expect(screen.getByText(/category: Dining/).textContent).not.toContain("Average");
  });

  it("renders a field-by-field diff when the call carries before/after values", async () => {
    stubApiWith([
      {
        id: "ap4",
        conversationId: "c1",
        moduleId: "finance",
        toolName: "update_budget",
        argumentsJson:
          '{"before":{"amount":300,"period":"monthly"},"after":{"amount":400,"period":"monthly"},"budgetId":"b-1"}',
        createdAt: "2026-07-11",
        risk: "high",
      },
    ]);
    renderApprovals();

    const diff = await screen.findByTestId("approval-diff");
    expect(diff.textContent).toContain("amount");
    expect(diff.textContent).toContain("300");
    expect(diff.textContent).toContain("400");
    // before/after left the plain-argument line; the untouched id stays.
    expect(screen.getByText(/budgetId: b-1/)).toBeTruthy();
  });

  it("an item without a risk tier fails safe to the full card", async () => {
    stubApiWith([
      {
        id: "ap5",
        conversationId: "c1",
        moduleId: "finance",
        toolName: "transfer_funds",
        argumentsJson: '{"amount":9000}',
        createdAt: "2026-07-11",
      },
    ]);
    renderApprovals();

    expect(await screen.findByTestId("approval-card")).toBeTruthy();
    expect(screen.queryByTestId("approval-compact")).toBeNull();
  });
});
