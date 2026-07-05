// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RolesEditor } from "./RolesEditor";

const ROLES = [
  { role: "system_admin", permissions: ["*"], editable: false, builtIn: true },
  { role: "user", permissions: ["chat.use"], editable: true, builtIn: true },
  { role: "auditor", permissions: ["platform.audit.view"], editable: true, builtIn: false },
];

const CATALOG = {
  platform: [
    { permission: "chat.use", category: "Chat & agents", description: "Start conversations.", requiresApproval: false, audited: false },
    { permission: "platform.users.manage", category: "Platform administration", description: "Manage users.", requiresApproval: false, audited: false },
  ],
  modules: [
    {
      id: "finance",
      displayName: "Finance",
      tools: [
        { permission: "tools.finance.summarize_spending", category: "Tool · Finance", description: "Summarize spending.", requiresApproval: false, audited: false },
      ],
    },
  ],
};

/** Routes the admin API calls RolesEditor makes; returns the fetch mock so PUTs can be asserted. */
function stubApi() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/admin/roles") && method === "GET") {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(ROLES) } as unknown as Response);
    }
    if (url.includes("/api/admin/security/catalog")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(CATALOG) } as unknown as Response);
    }
    // PUT (save) and anything else.
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderEditor() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RolesEditor />
    </QueryClientProvider>,
  );
}

describe("RolesEditor (schema-driven)", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("renders the role's permission toggles from the live catalog", async () => {
    stubApi();
    renderEditor();

    // Pick an editable role to reveal the editor.
    fireEvent.click(await screen.findByRole("button", { name: "user" }));

    // Every catalog permission (platform built-ins + each module's tools) appears as a toggle — no
    // hardcoding. Each also appears in the embedded permission-reference table, hence getAllByText.
    expect((await screen.findAllByText("chat.use")).length).toBeGreaterThan(0);
    expect(screen.getAllByText("platform.users.manage").length).toBeGreaterThan(0);
    expect(screen.getAllByText("tools.finance.summarize_spending").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Finance.*tools/i).length).toBeGreaterThan(0);

    // The former Security page now lives here as a collapsible reference.
    expect(screen.getByText(/Permission reference/i)).toBeTruthy();
  });

  it("saves a toggled permission as a PUT carrying the updated set", async () => {
    const fetchMock = stubApi();
    renderEditor();

    fireEvent.click(await screen.findByRole("button", { name: "user" }));
    // Grant the user role an extra permission it didn't have.
    fireEvent.click(await screen.findByRole("checkbox", { name: /platform\.users\.manage/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) => String(c[0]).includes("/api/admin/roles/user/permissions") && (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      expect(put).toBeTruthy();
      const body = JSON.parse((put![1] as RequestInit).body as string) as { permissions: string[] };
      expect(body.permissions).toContain("chat.use");
      expect(body.permissions).toContain("platform.users.manage");
    });
  });

  it("deletes a custom role only after confirming through the dialog", async () => {
    const fetchMock = stubApi();
    renderEditor();

    // The custom (non-built-in) role offers deletion.
    fireEvent.click(await screen.findByRole("button", { name: /auditor/ }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete role" }));

    // Cancelling the dialog leaves the role untouched…
    let dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(
      fetchMock.mock.calls.some((c) => (c[1] as RequestInit | undefined)?.method === "DELETE"),
    ).toBe(false);

    // …confirming issues the DELETE.
    fireEvent.click(screen.getByRole("button", { name: "Delete role" }));
    dialog = await screen.findByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete role" }));

    await waitFor(() =>
      expect(
        fetchMock.mock.calls.some(
          (c) => String(c[0]).includes("/api/admin/roles/auditor") && (c[1] as RequestInit | undefined)?.method === "DELETE",
        ),
      ).toBe(true),
    );
  });

  it("shows system_admin as a locked, read-only role", async () => {
    stubApi();
    renderEditor();

    // system_admin is the default (first) role and is not editable.
    expect(await screen.findByText(/always holds the global wildcard/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Save changes" })).toBeNull();
  });
});
