// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { BottomNav } from "./BottomNav";
import type { ModuleTab } from "../lib/api";

/**
 * Mirrors GenericTabCards.test.tsx: jsdom has no real matchMedia, so the narrow-viewport suite
 * stubs one that matches — and the desktop test runs WITHOUT a stub, pinning that the bar renders
 * nothing at all when the viewport isn't narrow (the hook reads absent matchMedia as no-match).
 */
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

function tab(n: number, extra?: Partial<ModuleTab>): ModuleTab {
  return { id: `t${n}`, label: `Tab ${n}`, route: `/m/t${n}`, ...extra };
}

const manyTabs = [1, 2, 3, 4, 5, 6].map((n) => tab(n));

function renderNav(
  tabs: ModuleTab[],
  opts: { onMore?: () => void; moreOpen?: boolean; at?: string } = {},
) {
  return render(
    <MemoryRouter
      initialEntries={[opts.at ?? "/"]}
      future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
    >
      <BottomNav tabs={tabs} onMore={opts.onMore ?? (() => {})} moreOpen={opts.moreOpen} />
    </MemoryRouter>,
  );
}

describe("BottomNav", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders nothing on a desktop viewport (no matchMedia stub)", () => {
    renderNav(manyTabs);
    expect(screen.queryByRole("navigation")).toBeNull();
  });

  it("shows the first four destinations plus a More button when tabs overflow", () => {
    stubNarrowViewport();
    renderNav(manyTabs);

    const nav = screen.getByRole("navigation", { name: "Tab bar" });
    expect(nav).toBeTruthy();
    for (const n of [1, 2, 3, 4]) {
      expect(screen.getByRole("link", { name: `Tab ${n}` })).toBeTruthy();
    }
    expect(screen.queryByRole("link", { name: "Tab 5" })).toBeNull();
    expect(screen.queryByRole("link", { name: "Tab 6" })).toBeNull();
    expect(screen.getByRole("button", { name: "More" }).getAttribute("aria-expanded")).toBe("false");
  });

  it("shows all tabs and omits More when five or fewer fit", () => {
    stubNarrowViewport();
    renderNav(manyTabs.slice(0, 5));

    for (const n of [1, 2, 3, 4, 5]) {
      expect(screen.getByRole("link", { name: `Tab ${n}` })).toBeTruthy();
    }
    expect(screen.queryByRole("button", { name: "More" })).toBeNull();
  });

  it("invokes onMore when More is tapped, and reflects the open drawer via aria-expanded", () => {
    stubNarrowViewport();
    const onMore = vi.fn();
    const { unmount } = renderNav(manyTabs, { onMore });

    fireEvent.click(screen.getByRole("button", { name: "More" }));
    expect(onMore).toHaveBeenCalledTimes(1);
    unmount();

    renderNav(manyTabs, { moreOpen: true });
    expect(screen.getByRole("button", { name: "More" }).getAttribute("aria-expanded")).toBe("true");
  });

  it("marks the active tab with aria-current and a non-color indicator", () => {
    stubNarrowViewport();
    renderNav(manyTabs, { at: "/m/t2" });

    const active = screen.getByRole("link", { name: "Tab 2" });
    expect(active.getAttribute("aria-current")).toBe("page");
    // The active affordance is never color-only: an indicator element exists on the active item…
    expect(active.querySelector("[data-active-indicator]")).toBeTruthy();
    // …and only there.
    const inactive = screen.getByRole("link", { name: "Tab 1" });
    expect(inactive.getAttribute("aria-current")).toBeNull();
    expect(inactive.querySelector("[data-active-indicator]")).toBeNull();
  });

  it("resolves manifest icon names and falls back to the neutral glyph otherwise", () => {
    stubNarrowViewport();
    renderNav([
      tab(1, { icon: "wallet" }),
      tab(2), // no icon declared
      tab(3, { icon: "not-a-known-glyph" }),
    ]);

    expect(
      screen.getByRole("link", { name: "Tab 1" }).querySelector('svg[data-icon="wallet"]'),
    ).toBeTruthy();
    expect(
      screen.getByRole("link", { name: "Tab 2" }).querySelector('svg[data-icon="fallback"]'),
    ).toBeTruthy();
    expect(
      screen.getByRole("link", { name: "Tab 3" }).querySelector('svg[data-icon="fallback"]'),
    ).toBeTruthy();
  });

  it("renders nothing when there are no tabs", () => {
    stubNarrowViewport();
    renderNav([]);
    expect(screen.queryByRole("navigation")).toBeNull();
  });
});
