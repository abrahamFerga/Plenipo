// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { AdminConsoleShell } from "./AdminConsoleShell";

const CATALOG = {
  platform: [
    { permission: "platform.users.manage", category: "Platform administration", description: "Manage users.", requiresApproval: false, audited: false },
  ],
  modules: [],
};

/** Routes /me (to set the caller's permissions) plus the security catalog the admin landing page loads. */
function stubApi(permissions: string[], displayName = "Operator") {
  const fetchMock = vi.fn((input: RequestInfo | URL) => {
    const url = String(input);
    if (url.includes("/api/platform/me")) {
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ userId: "u1", displayName, tenantId: "t1", permissions }),
      } as unknown as Response);
    }
    if (url.includes("/api/admin/security/catalog")) {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(CATALOG) } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve([]) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
}

function renderShell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <AdminConsoleShell />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("AdminConsoleShell (client-side admin gate)", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("refuses a caller without any platform permission", async () => {
    stubApi(["chat.use"], "Bob");
    renderShell();

    expect(await screen.findByText(/do not have permission to administer/i)).toBeTruthy();
    // The chrome still renders the signed-in user.
    expect(screen.getByText("Bob")).toBeTruthy();
  });

  it("renders the administration surface for a platform admin", async () => {
    stubApi(["platform.users.manage"], "Admin");
    renderShell();

    // The gate opens: the AdminPage navigation (and its landing security view) appear.
    expect(await screen.findByText("Administration")).toBeTruthy();
  });
});
