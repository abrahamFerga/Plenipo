// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router-dom";
import { ConnectedAccounts } from "./ConnectedAccounts";

const msgraph = {
  id: "msgraph",
  displayName: "Microsoft 365",
  description: "Browse and fetch the user's OneDrive/SharePoint documents.",
  icon: "briefcase",
  connected: false,
};

function renderAccounts(handler: (url: string, init?: RequestInit) => unknown) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) =>
    Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(handler(String(input), init)),
    } as unknown as Response),
  );
  vi.stubGlobal("fetch", fetchMock);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      {/* Router context for the page's link to its account-area sibling (/account/ai-decisions). */}
      <MemoryRouter>
        <ConnectedAccounts />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return fetchMock;
}

describe("ConnectedAccounts (per-user delegated connector logins)", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("lists enabled delegated connectors and shows an empty state when there are none", async () => {
    renderAccounts(() => []);
    expect(
      await screen.findByText(/No connectable data sources are enabled/),
    ).toBeTruthy();
  });

  it("Connect fetches the authorize URL and opens it in a new tab", async () => {
    const open = vi.fn();
    vi.stubGlobal("open", open);
    const fetchMock = renderAccounts((url) =>
      url.endsWith("/oauth/start") ? { authorizeUrl: "https://login.example/authorize?x=1" } : [msgraph],
    );

    fireEvent.click(await screen.findByRole("button", { name: "Connect" }));

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some((c) => String(c[0]).endsWith("/api/connectors/msgraph/oauth/start")),
      ).toBe(true);
      expect(open).toHaveBeenCalledWith("https://login.example/authorize?x=1", "_blank", "noopener");
    });
  });

  it("links to the account area's ADMT disclosure page (AI decision history)", async () => {
    renderAccounts(() => [msgraph]);

    const link = await screen.findByRole("link", { name: "AI decision history" });
    expect(link.getAttribute("href")).toBe("/account/ai-decisions");
  });

  it("a connected account shows the badge and Disconnect DELETEs only my login", async () => {
    const fetchMock = renderAccounts(() => [{ ...msgraph, connected: true }]);

    expect(await screen.findByText("Connected")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Disconnect" }));

    await waitFor(() => {
      const del = fetchMock.mock.calls.find(
        (c) => (c[1] as RequestInit | undefined)?.method === "DELETE",
      );
      expect(del).toBeTruthy();
      expect(String(del![0])).toContain("/api/connectors/msgraph/login");
    });
  });
});
