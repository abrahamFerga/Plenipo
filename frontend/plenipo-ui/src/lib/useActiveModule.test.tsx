// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { ActiveModuleContext, useActiveModule } from "./activeModule";
import type { ActiveModuleContextValue } from "./activeModule";

const value: ActiveModuleContextValue = {
  modules: [],
  activeModule: undefined,
  activeModuleId: "finance",
  setActiveModuleId: vi.fn(),
};

describe("useActiveModule", () => {
  it("exposes the active-module context (and lets a host switch modules) inside the provider", () => {
    const { result } = renderHook(() => useActiveModule(), {
      wrapper: ({ children }: { children: ReactNode }) => (
        <ActiveModuleContext.Provider value={value}>{children}</ActiveModuleContext.Provider>
      ),
    });

    expect(result.current.activeModuleId).toBe("finance");
    result.current.setActiveModuleId("nutrition");
    expect(value.setActiveModuleId).toHaveBeenCalledWith("nutrition");
  });

  it("throws a clear error when used outside a provider", () => {
    vi.spyOn(console, "error").mockImplementation(() => {}); // silence React's caught-error log
    expect(() => renderHook(() => useActiveModule())).toThrow("useActiveModule must be used within");
    vi.restoreAllMocks();
  });
});
