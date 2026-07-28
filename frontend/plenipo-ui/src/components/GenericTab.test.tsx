// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { GenericTab } from "./GenericTab";
import type { ModuleTab } from "../lib/api";

const foodsTab: ModuleTab = {
  id: "foods",
  label: "Foods",
  route: "/nutrition/foods",
  dataEndpoint: "/api/nutrition/foods",
  columns: [
    { field: "name", header: "Food" },
    { field: "kcalPer100g", header: "kcal/100g" },
  ],
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

describe("GenericTab (server-driven table)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders the declared column headers and the endpoint's rows", async () => {
    renderTab(foodsTab, [{ name: "Chicken breast", kcalPer100g: 165 }]);

    expect(await screen.findByText("Chicken breast")).toBeTruthy();
    expect(screen.getByText("Food")).toBeTruthy(); // a column header from the manifest
    expect(screen.getByText("165")).toBeTruthy(); // a numeric cell, stringified
  });

  it("shows an empty state when the endpoint returns no rows", async () => {
    renderTab(foodsTab, []);

    expect(await screen.findByText("No data yet.")).toBeTruthy();
  });

  it("stays read-only when the tab declares no editor", async () => {
    renderTab(foodsTab, [{ name: "Chicken breast", kcalPer100g: 165 }]);

    await screen.findByText("Chicken breast");
    expect(screen.queryByRole("button", { name: "Add" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });

  const clausesTab: ModuleTab = {
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
        { field: "template", label: "Clause text", multiline: true },
      ],
    },
  };

  it("with an editor: Add opens the form and Save POSTs the field values", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      void init;
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve([{ slug: "confidentiality", title: "Confidentiality", template: "Keep it secret." }]),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={clausesTab} />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    fireEvent.change(screen.getByLabelText("Type"), { target: { value: "data-protection" } });
    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Data Protection" } });
    fireEvent.change(screen.getByLabelText("Clause text"), { target: { value: "Handle data lawfully." } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        (c) => String(c[0]).endsWith("/api/legal/clauses") && (c[1] as RequestInit | undefined)?.method === "POST",
      );
      expect(post).toBeTruthy();
      expect(JSON.parse((post![1] as RequestInit).body as string)).toEqual({
        slug: "data-protection",
        title: "Data Protection",
        template: "Handle data lawfully.",
      });
    });
  });

  it("numeric fields post JSON numbers; empty optional fields are omitted, not sent as \"\"", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      void init;
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve([]),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab
          tab={{
            id: "budgets",
            label: "Budgets",
            route: "/finance/budgets",
            dataEndpoint: "/api/finance/budgets",
            columns: [{ field: "category", header: "Category" }],
            editor: {
              upsertEndpoint: "/api/finance/budgets",
              keyField: "category",
              fields: [
                { field: "category", label: "Category" },
                { field: "monthlyLimit", label: "Monthly limit", numeric: true },
                { field: "currency", label: "Currency", required: false },
              ],
            },
          }}
        />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    fireEvent.change(screen.getByLabelText("Category"), { target: { value: "Dining" } });
    fireEvent.change(screen.getByLabelText("Monthly limit"), { target: { value: "2500.5" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find((c) => (c[1] as RequestInit | undefined)?.method === "POST");
      expect(post).toBeTruthy();
      // The decimal-bound endpoint gets a real number — not "2500.5" — and the untouched optional
      // currency field is absent entirely (a "" would bind poorly; Number("") would post 0).
      expect(JSON.parse((post![1] as RequestInit).body as string)).toEqual({
        category: "Dining",
        monthlyLimit: 2500.5,
      });
    });
  });

  it("with a detailEndpoint: View fetches the resolved URL and renders the detail document; Back returns", async () => {
    const detail = {
      title: "Vandelay acquisition",
      subtitle: "Open · Client: Vandelay",
      sections: [
        { heading: "Time", text: "2h total, 1.5h billable." },
        {
          heading: "Parties",
          columns: [{ field: "name", header: "Name" }],
          rows: [{ name: "Kruger" }],
        },
      ],
    };
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void init;
      const url = String(input);
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            url.includes("/detail") ? detail : [{ id: "m-1", name: "Vandelay acquisition", status: "Open" }],
          ),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab
          tab={{
            id: "matters",
            label: "Matters",
            route: "/legal/matters",
            dataEndpoint: "/api/legal/matters",
            columns: [{ field: "name", header: "Matter" }],
            detailEndpoint: "/api/legal/matters/{id}/detail",
          }}
        />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "View" }));

    expect(await screen.findByText("Open · Client: Vandelay")).toBeTruthy();
    expect(screen.getByText("2h total, 1.5h billable.")).toBeTruthy();
    expect(screen.getByText("Kruger")).toBeTruthy();
    expect(
      fetchMock.mock.calls.some((c) => String(c[0]).endsWith("/api/legal/matters/m-1/detail")),
    ).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "← Back" }));
    expect(await screen.findByRole("button", { name: "View" })).toBeTruthy(); // the table is back
  });

  const batchesTab: ModuleTab = {
    id: "review",
    label: "Review",
    route: "/finance/review",
    dataEndpoint: "/api/finance/imports/batches",
    columns: [{ field: "fileName", header: "Statement" }],
    detailEndpoint: "/api/finance/imports/{id}/detail",
  };

  it("detail actions: the button POSTs (after confirm), surfaces the message visibly, and refreshes the document", async () => {
    let approved = false;
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if ((init?.method ?? "GET") === "POST") {
        approved = true;
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({ message: "Posted 12 transaction(s) from 'may.pdf'." }),
        } as unknown as Response);
      }
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            url.includes("/detail")
              ? {
                  title: "may.pdf",
                  subtitle: approved ? "approved · 12 line(s)" : "parsed · 12 line(s)",
                  sections: [],
                  // Approval leaves nothing else to do — the refreshed document has NO actions,
                  // and the success message must survive that (the vanishing-banner regression).
                  actions: approved
                    ? []
                    : [
                        {
                          id: "approve",
                          label: "Approve",
                          endpoint: "/api/finance/imports/b-1/approve",
                          confirm: "Post this batch's lines?",
                        },
                      ],
                }
              : [{ id: "b-1", fileName: "may.pdf" }],
          ),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={batchesTab} />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "View" }));
    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    // The confirm dialog interposes; its confirm button carries the action label.
    expect(screen.getByText("Post this batch's lines?")).toBeTruthy();
    const dialogButtons = screen.getAllByRole("button", { name: "Approve" });
    fireEvent.click(dialogButtons[dialogButtons.length - 1]);

    expect((await screen.findByTestId("detail-action-message")).textContent).toContain(
      "Posted 12 transaction(s) from 'may.pdf'.",
    );
    expect(
      fetchMock.mock.calls.some(
        (c) => String(c[0]).endsWith("/api/finance/imports/b-1/approve") && (c[1] as RequestInit)?.method === "POST",
      ),
    ).toBe(true);
    // The document refetched after the action: the subtitle now reflects the new status, the
    // approve button is gone (no actions remain) — and the message banner is STILL shown.
    expect(await screen.findByText("approved · 12 line(s)")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
    expect(screen.getByTestId("detail-action-message").textContent).toContain("Posted 12 transaction(s)");
  });

  it("detail actions: a field-carrying action stays disabled until chosen, then POSTs the value as its body", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if ((init?.method ?? "GET") === "POST") {
        return Promise.resolve({
          ok: true,
          json: () => Promise.resolve({ message: "Assigned to 'Checking'." }),
        } as unknown as Response);
      }
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            url.includes("/detail")
              ? {
                  title: "may.pdf",
                  subtitle: "needs-account",
                  sections: [],
                  actions: [
                    {
                      id: "assign",
                      label: "Assign",
                      endpoint: "/api/finance/imports/b-1/assign-account",
                      field: { field: "accountName", label: "Account", options: [{ value: "Checking", label: "Checking" }] },
                    },
                  ],
                }
              : [{ id: "b-1", fileName: "may.pdf" }],
          ),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={batchesTab} />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "View" }));

    const assign = await screen.findByRole("button", { name: "Assign" });
    expect((assign as HTMLButtonElement).disabled).toBe(true); // no account picked yet

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "Checking" } });
    expect((assign as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(assign);

    await waitFor(() => {
      const post = fetchMock.mock.calls.find((c) => (c[1] as RequestInit)?.method === "POST");
      expect(post).toBeTruthy();
      expect(String(post![0])).toContain("/api/finance/imports/b-1/assign-account");
      expect(JSON.parse(String((post![1] as RequestInit).body))).toEqual({ accountName: "Checking" });
    });
    expect((await screen.findByTestId("detail-action-message")).textContent).toContain("Assigned to 'Checking'.");
  });

  it("detail actions: a refusing endpoint (409) renders the reason as an error banner, and Back survives a vanished record", async () => {
    let discarded = false;
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if ((init?.method ?? "GET") === "POST") {
        discarded = true;
        return Promise.resolve({
          ok: false,
          status: 409,
          statusText: "Conflict",
          text: () => Promise.resolve(JSON.stringify({ message: "'may.pdf' has no account yet." })),
        } as unknown as Response);
      }
      if (url.includes("/detail") && discarded) {
        return Promise.resolve({
          ok: false,
          status: 404,
          statusText: "Not Found",
          text: () => Promise.resolve(""),
        } as unknown as Response);
      }
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            url.includes("/detail")
              ? {
                  title: "may.pdf",
                  subtitle: "needs-account",
                  sections: [],
                  actions: [{ id: "approve", label: "Approve", endpoint: "/api/finance/imports/b-1/approve" }],
                }
              : [{ id: "b-1", fileName: "may.pdf" }],
          ),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={batchesTab} />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "View" }));
    fireEvent.click(await screen.findByRole("button", { name: "Approve" })); // no confirm declared — runs directly

    const banner = await screen.findByTestId("detail-action-message");
    expect(banner.textContent).toContain("'may.pdf' has no account yet.");
    // The refetch 404s (record gone in this contrived flow) — the Back button must still render.
    expect(await screen.findByRole("button", { name: "← Back" })).toBeTruthy();
  });

  it("row actions: the button POSTs the {field}-resolved URL (after confirm) and surfaces the message", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      const isPost = (init as RequestInit | undefined)?.method === "POST";
      return Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            isPost
              ? { message: "Batch b-1 approved: 3 line(s) posted." }
              : [{ id: "b-1", fileName: "june.pdf", lines: 3 }],
          ),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab
          tab={{
            id: "review",
            label: "Statement review",
            route: "/finance/review",
            dataEndpoint: "/api/finance/imports/batches",
            columns: [{ field: "fileName", header: "File" }],
            rowActions: [
              {
                id: "approve",
                label: "Approve",
                endpointTemplate: "/api/finance/imports/{id}/approve",
                confirm: "Post this batch's lines as transactions?",
              },
            ],
          }}
        />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Approve" }));
    // Consequential: the confirm dialog gates the POST — its confirm button reuses the label.
    expect(screen.getByText("Post this batch's lines as transactions?")).toBeTruthy();
    const approves = screen.getAllByRole("button", { name: "Approve" });
    fireEvent.click(approves[approves.length - 1]);

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        (c) =>
          String(c[0]).endsWith("/api/finance/imports/b-1/approve") &&
          (c[1] as RequestInit | undefined)?.method === "POST",
      );
      expect(post).toBeTruthy();
    });
    expect(await screen.findByText("Batch b-1 approved: 3 line(s) posted.")).toBeTruthy();
  });

  it("resolves every {field} placeholder in a template, not just the first", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      const isPost = (init as RequestInit | undefined)?.method === "POST";
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve(isPost ? {} : [{ batchId: "b-2", index: 5, memo: "coffee" }]),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab
          tab={{
            id: "lines",
            label: "Lines",
            route: "/finance/lines",
            dataEndpoint: "/api/finance/lines",
            columns: [{ field: "memo", header: "Memo" }],
            rowActions: [
              { id: "drop", label: "Drop", endpointTemplate: "/api/finance/imports/{batchId}/lines/{index}/drop" },
            ],
          }}
        />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Drop" })); // no confirm — fires directly

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some((c) => String(c[0]).endsWith("/api/finance/imports/b-2/lines/5/drop")),
      ).toBe(true);
    });
  });

  it("masked columns render bullets with the last four characters, revealing only on demand", async () => {
    renderTab(
      {
        ...foodsTab,
        columns: [
          { field: "name", header: "Account" },
          { field: "number", header: "Number", masked: true },
        ],
      },
      [{ name: "Everyday checking", number: "12345678" }],
    );

    await screen.findByText("Everyday checking");
    // The raw value is not in the document — only the masked form.
    expect(screen.queryByText("12345678")).toBeNull();
    expect(screen.getByText("••••5678")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Reveal Number" }));
    expect(screen.getByText("12345678")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Hide Number" }));
    expect(screen.queryByText("12345678")).toBeNull();
  });

  it("masks short values entirely — four bullets leak nothing", async () => {
    renderTab(
      {
        ...foodsTab,
        columns: [{ field: "pin", header: "PIN", masked: true }],
      },
      [{ pin: "1234" }],
    );

    expect(await screen.findByText("••••")).toBeTruthy();
    expect(screen.queryByText("1234")).toBeNull();
  });

  it("with an editor: Edit prefills from the row and locks the key field; Delete DELETEs the resolved URL", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      void input;
      void init;
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve([{ slug: "confidentiality", title: "Confidentiality", template: "Keep it secret." }]),
      } as unknown as Response);
    });
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={clausesTab} />
      </QueryClientProvider>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    const key = screen.getByLabelText("Type") as HTMLInputElement;
    expect(key.value).toBe("confidentiality");
    expect(key.disabled).toBe(true); // the key is the record identity — not editable
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    // The dialog is open: its confirm is the LAST Delete button in the tree.
    const deletes = screen.getAllByRole("button", { name: "Delete" });
    fireEvent.click(deletes[deletes.length - 1]);

    await waitFor(() => {
      const del = fetchMock.mock.calls.find(
        (c) =>
          String(c[0]).endsWith("/api/legal/clauses/confidentiality") &&
          (c[1] as RequestInit | undefined)?.method === "DELETE",
      );
      expect(del).toBeTruthy();
    });
  });
});

describe("GenericTab (singleton form)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  const settingsTab: ModuleTab = {
    id: "settings",
    label: "Settings",
    route: "/finance/settings",
    dataEndpoint: "/api/finance/settings",
    singleton: true,
    columns: [
      { field: "defaultCurrencyCode", header: "Default currency" },
      { field: "timeZoneId", header: "Time zone" },
    ],
    // The wire TabEditor carries no permission — the server strips the editor entirely from the
    // payload for callers who lack it, so its mere presence means "this caller may manage".
    editor: {
      upsertEndpoint: "/api/finance/settings",
      fields: [
        { field: "defaultCurrencyCode", label: "Default currency", group: "Currency & locale",
          options: [{ value: "USD", label: "USD" }, { value: "MXN", label: "MXN" }] },
        { field: "billReminderLeadDays", label: "Bill reminder lead (days)", required: false, numeric: true,
          group: "Reminders" },
      ],
    },
  };

  it("renders a labeled form (not a table) prefilled from the single config row", async () => {
    renderTab(settingsTab, [{ defaultCurrencyCode: "MXN", billReminderLeadDays: 5 }]);

    // A form, not a grid: the currency select is prefilled from the row, and there is no Add button.
    await screen.findByLabelText("Default currency");
    expect((screen.getByLabelText("Default currency") as HTMLSelectElement).value).toBe("MXN");
    expect((screen.getByLabelText("Bill reminder lead (days)") as HTMLInputElement).value).toBe("5");
    expect(screen.queryByText("Add")).toBeNull();
    expect(document.querySelector("table")).toBeNull();
  });

  it("groups fields under their section headings", async () => {
    renderTab(settingsTab, [{ defaultCurrencyCode: "USD" }]);
    await screen.findByLabelText("Default currency");
    expect(screen.getByText("Currency & locale")).toBeTruthy();
    expect(screen.getByText("Reminders")).toBeTruthy();
  });

  it("saves the whole config to the upsert endpoint", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve([{ defaultCurrencyCode: "USD" }]),
    } as unknown as Response);
    vi.stubGlobal("fetch", fetchMock);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={client}>
        <GenericTab tab={settingsTab} />
      </QueryClientProvider>,
    );

    fireEvent.change(await screen.findByLabelText("Default currency"), { target: { value: "MXN" } });
    fireEvent.click(screen.getByRole("button", { name: /save/i }));

    await waitFor(() => {
      const post = fetchMock.mock.calls.find(
        (c) => String(c[0]).endsWith("/api/finance/settings") && (c[1] as RequestInit)?.method === "POST",
      );
      expect(post).toBeTruthy();
      expect(JSON.parse((post![1] as RequestInit).body as string).defaultCurrencyCode).toBe("MXN");
    });
    expect(await screen.findByTestId("settings-saved")).toBeTruthy();
  });

  it("shows read-only values when the caller lacks the editor (no manage permission)", async () => {
    const readOnly: ModuleTab = { ...settingsTab, editor: null };
    renderTab(readOnly, [{ defaultCurrencyCode: "USD", timeZoneId: "UTC" }]);

    // The value shows against its column label, and there is no editable control or Save button.
    expect(await screen.findByText("USD")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /save/i })).toBeNull();
    expect(screen.queryByRole("combobox")).toBeNull();
  });
});
