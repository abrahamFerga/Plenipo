// @vitest-environment jsdom
import { lazy, type ComponentType } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { ModuleTabView } from "./ModuleTabView";
import type { ModuleTab } from "../lib/api";
import type { ModuleTabProps } from "../lib/moduleUi";

const tab: ModuleTab = {
  id: "transactions",
  label: "Transactions",
  route: "/finance/transactions",
};

describe("ModuleTabView", () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("renders the host-registered component (with module + tab), not the generic view", () => {
    const Custom = ({ moduleId, tab: t }: ModuleTabProps) => (
      <div>
        custom:{moduleId}/{t.id}
      </div>
    );
    render(<ModuleTabView moduleId="finance" tab={tab} component={Custom} />);

    expect(screen.getByText("custom:finance/transactions")).toBeTruthy();
    // The generic renderer's tell-tale label heading must be absent.
    expect(screen.queryByRole("heading", { name: "Transactions" })).toBeNull();
  });

  it("falls back to the server-driven GenericTab when no component is registered", () => {
    render(<ModuleTabView moduleId="finance" tab={tab} />);

    // GenericTab renders the tab label as a heading and a placeholder (no dataEndpoint here).
    expect(screen.getByRole("heading", { name: "Transactions" })).toBeTruthy();
    expect(screen.getByText("Nothing to show here yet.")).toBeTruthy();
  });

  it("isolates a crashing host component behind the tab error boundary", () => {
    vi.spyOn(console, "error").mockImplementation(() => {}); // silence React's caught-error log
    const Boom = () => {
      throw new Error("host bug");
    };
    render(<ModuleTabView moduleId="finance" tab={tab} component={Boom} />);

    expect(screen.getByText("This view failed to load: Transactions")).toBeTruthy();
    expect(screen.getByText("host bug")).toBeTruthy();
  });

  it("renders a lazily-loaded host component through the Suspense boundary", async () => {
    const Lazy = lazy(async () => ({ default: () => <div>lazy loaded</div> }));
    render(
      <ModuleTabView
        moduleId="finance"
        tab={tab}
        component={Lazy as unknown as ComponentType<ModuleTabProps>}
      />,
    );

    expect(await screen.findByText("lazy loaded")).toBeTruthy();
  });
});
