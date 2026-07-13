import { describe, expect, it } from "vitest";
import { moduleIdForPath, resolveActiveModuleId } from "./activeModule";
import type { Module } from "./api";

const modules: Module[] = [
  {
    id: "finance",
    displayName: "Finance",
    tabs: [
      { id: "chat", label: "Chat", route: "/chat" },
      { id: "transactions", label: "Transactions", route: "/finance/transactions" },
    ],
  },
  {
    id: "nutrition",
    displayName: "Nutrition",
    tabs: [{ id: "foods", label: "Foods", route: "/nutrition/foods" }],
  },
];

describe("moduleIdForPath", () => {
  it("maps a tab route to its module", () => {
    expect(moduleIdForPath(modules, "/finance/transactions")).toBe("finance");
    expect(moduleIdForPath(modules, "/nutrition/foods")).toBe("nutrition");
  });

  it("returns undefined for the module-agnostic /chat route", () => {
    expect(moduleIdForPath(modules, "/chat")).toBeUndefined();
  });

  it("returns undefined for an unknown path or a not-yet-loaded manifest", () => {
    expect(moduleIdForPath(modules, "/nope")).toBeUndefined();
    expect(moduleIdForPath(undefined, "/finance/transactions")).toBeUndefined();
  });
});

describe("resolveActiveModuleId", () => {
  it("prefers the module named by the URL — even over the selected one (deep links / refresh win)", () => {
    expect(resolveActiveModuleId(modules, "nutrition", "/finance/transactions")).toBe("finance");
  });

  it("falls back to the explicitly selected module on module-agnostic routes like /chat", () => {
    expect(resolveActiveModuleId(modules, "nutrition", "/chat")).toBe("nutrition");
  });

  it("falls back to the first module when nothing is selected and the path names none", () => {
    expect(resolveActiveModuleId(modules, undefined, "/chat")).toBe("finance");
  });

  it("returns undefined while the manifest is still loading", () => {
    expect(resolveActiveModuleId(undefined, undefined, "/chat")).toBeUndefined();
  });
});
