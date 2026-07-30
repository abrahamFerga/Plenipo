import { Text } from "react-native";
import { NavigationContainer } from "@react-navigation/native";
import { QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react-native";
import type { Module } from "@plenipo/client";
import { AppShell } from "./AppShell";
import { defineModule, createModuleUiRegistry, type ModuleTabProps } from "../lib/moduleUi";
import { fakeApi, testQueryClient } from "../test-support";

/**
 * The central claim of this package: navigation is a projection of `GET /api/platform/modules`,
 * with no route, screen, or vocabulary hardcoded. If these pass, installing a module in a C# host
 * genuinely does put it on every phone without a release.
 */

const api = fakeApi();

beforeEach(() => {
  api.install();
  // The header polls approvals and asks who the product is; give both a quiet answer.
  api.on("/api/chat/approvals", () => []);
  api.on("/api/platform/branding", () => ({ name: "Acme Ops" }));
});

afterEach(() => api.reset());

const legal: Module = {
  id: "legal",
  displayName: "Legal",
  tabs: [
    { id: "matters", label: "Matters", route: "/legal/matters", dataEndpoint: "/api/legal/matters" },
    { id: "deadlines", label: "Deadlines", route: "/legal/deadlines" },
  ],
};

function renderShell(props: Parameters<typeof AppShell>[0] = {}) {
  return render(
    <QueryClientProvider client={testQueryClient()}>
      <NavigationContainer>
        <AppShell {...props} />
      </NavigationContainer>
    </QueryClientProvider>,
  );
}

describe("navigation from the manifest", () => {
  it("builds the tab bar out of the module's tabs, chat first", async () => {
    api.on("/api/platform/modules", () => [legal]);

    renderShell();

    // Nothing here was written into the app: "Matters" exists because a C# manifest said so.
    expect(await screen.findByText("Chat")).toBeOnTheScreen();
    expect(screen.getByText("Matters")).toBeOnTheScreen();
    expect(screen.getByText("Deadlines")).toBeOnTheScreen();
  });

  it("shows a tab the caller can open and never invents one they can't", async () => {
    // The server omits tabs the caller lacks permission for — it does not send them hidden.
    api.on("/api/platform/modules", () => [{ ...legal, tabs: [legal.tabs[0]] }]);

    renderShell();

    expect(await screen.findByText("Matters")).toBeOnTheScreen();
    expect(screen.queryByText("Deadlines")).toBeNull();
  });

  it("collapses past five destinations into More rather than crushing the bar", async () => {
    api.on("/api/platform/modules", () => [
      {
        ...legal,
        tabs: ["matters", "deadlines", "time", "documents", "invoices", "reports"].map((id) => ({
          id,
          label: id,
          route: `/legal/${id}`,
        })),
      },
    ]);

    renderShell();

    expect(await screen.findByText("More")).toBeOnTheScreen();
    // Four destinations fit — Chat plus three — and the fifth slot becomes More.
    expect(screen.getByText("time")).toBeOnTheScreen();
    expect(screen.queryByText("documents")).toBeNull();
    expect(screen.queryByText("reports")).toBeNull();

    // The hidden tabs are still registered, so the sheet can reach them.
    fireEvent.press(screen.getByText("More"));
    expect(await screen.findByText("reports")).toBeOnTheScreen();
  });

  it("lands on a tab the manifest marks Home instead of chat", async () => {
    api.on("/api/legal/matters", () => []);
    api.on("/api/platform/modules", () => [
      { ...legal, tabs: [{ ...legal.tabs[0], home: true }, legal.tabs[1]] },
    ]);

    renderShell();

    // The Matters screen is the one mounted — its empty state proves it, not just its tab button.
    expect(await screen.findByText("No data yet.")).toBeOnTheScreen();
  });

  it("offers every enabled module in the switcher", async () => {
    api.on("/api/platform/modules", () => [
      legal,
      { id: "finance", displayName: "Finance", description: "Money", tabs: [] },
    ]);

    renderShell();
    fireEvent.press(await screen.findByLabelText("Switch module. Current: Legal"));

    expect(await screen.findByText("Finance")).toBeOnTheScreen();
  });

  it("says so plainly when the account has no modules", async () => {
    api.on("/api/platform/modules", () => []);

    renderShell();

    expect(await screen.findByText(/No modules are enabled/)).toBeOnTheScreen();
  });
});

describe("approvals", () => {
  it("keeps the waiting count in the header, where a blocked write can't be missed", async () => {
    api.on("/api/platform/modules", () => [legal]);
    api.on("/api/chat/approvals", () => [
      { id: "a1", conversationId: "c1", moduleId: "legal", toolName: "file_motion", createdAt: "2026-07-29T10:00:00Z" },
      { id: "a2", conversationId: "c1", moduleId: "legal", toolName: "send_letter", createdAt: "2026-07-29T10:01:00Z" },
    ]);

    renderShell();

    expect(await screen.findByLabelText("Approvals, 2 waiting")).toBeOnTheScreen();
  });

  it("opens the approvals screen from the header", async () => {
    api.on("/api/platform/modules", () => [legal]);
    api.on("/api/chat/approvals", () => []);

    renderShell();
    fireEvent.press(await screen.findByLabelText("Approvals"));

    expect(await screen.findByText(/Nothing waiting on you/)).toBeOnTheScreen();
  });
});

describe("the product-extension seam", () => {
  it("renders a registered native screen instead of the generic one", async () => {
    api.on("/api/platform/modules", () => [legal]);
    api.on("/api/legal/matters", () => []);

    function MattersBoard({ moduleId, tab }: ModuleTabProps) {
      return <Text>{`custom ${moduleId}/${tab.id}`}</Text>;
    }

    renderShell({
      moduleUi: createModuleUiRegistry([defineModule("legal", { tabs: { matters: MattersBoard } })]),
    });

    fireEvent.press(await screen.findByText("Matters"));

    expect(await screen.findByText("custom legal/matters")).toBeOnTheScreen();
  });

  it("leaves unregistered tabs on the generic renderer", async () => {
    api.on("/api/platform/modules", () => [legal]);
    api.on("/api/legal/matters", () => []);

    renderShell({ moduleUi: createModuleUiRegistry([]) });
    fireEvent.press(await screen.findByText("Matters"));

    // A module that needs no custom mobile UI costs zero React Native code.
    expect(await screen.findByText("No data yet.")).toBeOnTheScreen();
  });
});

describe("a tapped notification", () => {
  it("lands on the tab whose route the link falls under", async () => {
    api.on("/api/platform/modules", () => [legal]);
    api.on("/api/legal/matters", () => []);

    // The link points at a RECORD; the manifest only declares the list route it belongs to.
    renderShell({ pendingLink: "/legal/matters/42" });

    await waitFor(() => expect(screen.getByText("No data yet.")).toBeOnTheScreen());
  });

  it("stays put when the link matches nothing, rather than guessing", async () => {
    api.on("/api/platform/modules", () => [legal]);
    const onLinkHandled = jest.fn();

    renderShell({ pendingLink: "/somewhere/else", onLinkHandled });

    // Handled either way, so an unresolvable link isn't retried on every render.
    await waitFor(() => expect(onLinkHandled).toHaveBeenCalled());
  });
});
