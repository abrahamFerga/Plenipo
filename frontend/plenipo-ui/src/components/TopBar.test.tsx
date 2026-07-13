// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { TopBar } from "./TopBar";
import { BrandingContext, type PlenipoBranding } from "../lib/branding";
import { ActiveModuleContext } from "../lib/activeModule";

function renderTopBar(branding: PlenipoBranding = {}) {
  vi.stubGlobal(
    "fetch",
    vi.fn(() =>
      Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ userId: "u", displayName: "Dev", tenantId: "t", permissions: [] }),
      } as unknown as Response),
    ),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ActiveModuleContext.Provider
          value={{ modules: [], activeModule: undefined, activeModuleId: undefined, setActiveModuleId: () => {} }}
        >
          <BrandingContext.Provider value={branding}>
            <TopBar />
          </BrandingContext.Provider>
        </ActiveModuleContext.Provider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("TopBar branding", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the Plenipo name by default", () => {
    renderTopBar();
    expect(screen.getByText("Plenipo")).toBeTruthy();
  });

  it("shows a host's product name and logo instead when branding is provided", () => {
    renderTopBar({ name: "Acme Ops", logo: (<span>ACME-LOGO</span>) as ReactNode });

    expect(screen.getByText("Acme Ops")).toBeTruthy();
    expect(screen.getByText("ACME-LOGO")).toBeTruthy();
    expect(screen.queryByText("Plenipo")).toBeNull();
  });
});
