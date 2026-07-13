// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AppShell } from "./AppShell";
import { defineModule, type ModuleTabProps } from "../lib/moduleUi";

const manifest = [
  {
    id: "finance",
    displayName: "Finance",
    tabs: [
      { id: "transactions", label: "Transactions", route: "/finance/transactions" },
      { id: "reports", label: "Reports", route: "/finance/reports" },
    ],
  },
];

const json = (body: unknown) =>
  Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as unknown as Response);

function stubApi(chatEnabled = true) {
  vi.stubGlobal(
    "fetch",
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/api/platform/modules")) return json(manifest);
      if (url.includes("/api/platform/me"))
        return json({ userId: "u", displayName: "Dev", tenantId: "t", permissions: [] });
      if (url.includes("/api/platform/info")) return json({ chatEnabled, demoMode: false });
      return json(null);
    }),
  );
}

function stubModulesError(status: number) {
  vi.stubGlobal(
    "fetch",
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/api/platform/modules")) {
        return Promise.resolve({
          ok: false,
          status,
          statusText: status === 403 ? "Forbidden" : "Server Error",
          text: () => Promise.resolve(""),
        } as unknown as Response);
      }
      if (url.includes("/api/platform/info")) return json({ chatEnabled: true, demoMode: false });
      if (url.includes("/api/platform/me"))
        return json({ userId: "u", displayName: "Dev", tenantId: "t", permissions: [] });
      return json(null);
    }),
  );
}

function renderAt(path: string) {
  const Board = ({ moduleId, tab }: ModuleTabProps) => <div>{`board:${moduleId}:${tab.id}`}</div>;
  const finance = defineModule("finance", { tabs: { transactions: Board, reports: Board } });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter
        initialEntries={[path]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppShell moduleUi={[finance]} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("AppShell deep-linking", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders a deep-linked tab route instead of bouncing to /chat", async () => {
    stubApi();
    renderAt("/finance/transactions");

    // The active module is resolved from the URL on the first loaded render, so the tab's route
    // exists before the catch-all redirect can fire — the registered component renders.
    expect(await screen.findByText("board:finance:transactions")).toBeTruthy();
  });

  it("shows the Chat tab in the nav when chat is enabled", async () => {
    stubApi(true);
    renderAt("/finance/transactions"); // avoid /chat so ChatView (SignalR) isn't mounted

    await screen.findByText("board:finance:transactions");
    expect(screen.getByText("Chat")).toBeTruthy(); // the sidebar's Chat nav link
  });

  it("hides the Chat tab and lands on a module tab when chat is disabled (no AI provider)", async () => {
    stubApi(false);
    renderAt("/"); // default landing

    // With no chat, the default landing is the module's first tab, and there's no Chat nav link.
    expect(await screen.findByText("board:finance:transactions")).toBeTruthy();
    expect(screen.queryByText("Chat")).toBeNull();
  });

  it("lands on a declared Home tab instead of chat, with Chat still first in the nav", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/api/platform/modules"))
          return json([
            {
              id: "finance",
              displayName: "Finance",
              tabs: [
                // Declared AFTER another tab on purpose: home wins by flag, not position.
                { id: "transactions", label: "Transactions", route: "/finance/transactions" },
                { id: "reports", label: "Reports", route: "/finance/reports", home: true },
              ],
            },
          ]);
        if (url.includes("/api/platform/me"))
          return json({ userId: "u", displayName: "Dev", tenantId: "t", permissions: [] });
        if (url.includes("/api/platform/info")) return json({ chatEnabled: true, demoMode: false });
        return json(null);
      }),
    );
    renderAt("/"); // default landing, chat ENABLED

    expect(await screen.findByText("board:finance:reports")).toBeTruthy();
    expect(screen.getByText("Chat")).toBeTruthy(); // chat-first nav is unchanged
  });

  it("shows an access-denied screen (not 'unreachable') when the manifest load is forbidden", async () => {
    stubModulesError(403);
    renderAt("/");

    expect(await screen.findByText("You don't have access")).toBeTruthy();
    expect(screen.queryByText("Can't reach the Plenipo API")).toBeNull();
  });

  it("shows the unreachable screen for a non-auth failure", async () => {
    stubModulesError(500);
    renderAt("/");

    expect(await screen.findByText("Can't reach the Plenipo API")).toBeTruthy();
  });

  it("updates the document title when navigating to a module tab", async () => {
    stubApi();
    renderAt("/finance/transactions");
    await screen.findByText("board:finance:transactions");

    // The deep-linked tab names the page: "<tab label> · <product name>".
    expect(document.title).toBe("Transactions · Plenipo");

    // In-app navigation to another module tab retitles the document.
    fireEvent.click(screen.getByRole("link", { name: "Reports" }));
    await screen.findByText("board:finance:reports");
    expect(document.title).toBe("Reports · Plenipo");
  });

  it("exposes a skip-to-content link and a labelled main landmark", async () => {
    stubApi();
    renderAt("/finance/transactions");
    await screen.findByText("board:finance:transactions");

    const skip = screen.getByRole("link", { name: "Skip to content" });
    expect(skip.getAttribute("href")).toBe("#main-content");
    expect(screen.getByRole("main", { name: "Workspace" })).toBeTruthy();
  });
});

// The platform half of a consuming product's mobile-navigation ask: below md, primary navigation
// is a fixed bottom bar (first four destinations + More) and the drawer becomes the overflow
// surface. Desktop keeps today's DOM — the last test pins that.
describe("AppShell mobile bottom navigation", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  /** Same shape as GenericTabCards.test.tsx: jsdom has no matchMedia, so narrow tests stub one. */
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

  // Five module tabs + the injected Chat tab = six destinations, forcing the More overflow.
  const manyTabsManifest = [
    {
      id: "finance",
      displayName: "Finance",
      tabs: [
        { id: "one", label: "One", route: "/fin/one" },
        { id: "two", label: "Two", route: "/fin/two" },
        { id: "three", label: "Three", route: "/fin/three" },
        { id: "four", label: "Four", route: "/fin/four" },
        { id: "five", label: "Five", route: "/fin/five" },
      ],
    },
  ];

  function stubApiManyTabs() {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes("/api/platform/modules")) return json(manyTabsManifest);
        if (url.includes("/api/platform/me"))
          return json({ userId: "u", displayName: "Dev", tenantId: "t", permissions: [] });
        if (url.includes("/api/platform/info")) return json({ chatEnabled: true, demoMode: false });
        return json(null);
      }),
    );
  }

  it("narrow viewport: renders the bar with the first four destinations plus More", async () => {
    stubNarrowViewport();
    stubApiManyTabs();
    renderAt("/fin/one");

    const bar = await screen.findByRole("navigation", { name: "Tab bar" });
    // Chat first, then module tabs — the bar mirrors the sidebar's order exactly.
    for (const label of ["Chat", "One", "Two", "Three"]) {
      expect(within(bar).getByRole("link", { name: label })).toBeTruthy();
    }
    expect(within(bar).queryByRole("link", { name: "Four" })).toBeNull();
    expect(within(bar).getByRole("button", { name: "More" })).toBeTruthy();
  });

  it("More opens the existing drawer where every tab is reachable, and navigating closes it", async () => {
    stubNarrowViewport();
    stubApiManyTabs();
    renderAt("/fin/one");
    await screen.findByRole("navigation", { name: "Tab bar" });

    // Drawer closed: only the static sidebar nav is in the DOM (jsdom applies no md: CSS).
    expect(screen.getAllByRole("navigation", { name: "Module tabs" })).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "More" }));
    const navs = screen.getAllByRole("navigation", { name: "Module tabs" });
    expect(navs).toHaveLength(2); // the drawer joined the static sidebar
    expect(screen.getByRole("button", { name: "More" }).getAttribute("aria-expanded")).toBe("true");

    // A tab beyond the bar's first four is reachable in the drawer; navigating auto-closes it.
    fireEvent.click(within(navs[1]).getByRole("link", { name: "Five" }));
    await waitFor(() =>
      expect(screen.getAllByRole("navigation", { name: "Module tabs" })).toHaveLength(1),
    );
  });

  it("desktop (no matchMedia stub): no bottom bar, and no top-bar hamburger", async () => {
    stubApi();
    renderAt("/finance/transactions");
    await screen.findByText("board:finance:transactions");

    expect(screen.queryByRole("navigation", { name: "Tab bar" })).toBeNull();
    // The hamburger is intentionally gone at every width: on narrow viewports the bottom bar's
    // More button is the drawer's single entrance, and at md+ it was always CSS-hidden anyway.
    expect(screen.queryByRole("button", { name: "Open navigation" })).toBeNull();
  });
});
