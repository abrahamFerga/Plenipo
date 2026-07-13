import { describe, expect, it } from "vitest";
import {
  createModuleUiRegistry,
  defineModule,
  resolveTabComponent,
} from "./moduleUi";

// Stand-in tab components — never rendered here; these are pure-function tests.
const Board = () => null;
const List = () => null;

describe("module UI registry", () => {
  it("defineModule attaches the id to the UI contributions", () => {
    const finance = defineModule("finance", { tabs: { transactions: Board } });
    expect(finance.id).toBe("finance");
    expect(finance.tabs?.transactions).toBe(Board);
  });

  it("defineModule defaults to no tabs", () => {
    expect(defineModule("empty")).toEqual({ id: "empty" });
  });

  it("createModuleUiRegistry keys modules by id (last wins on a duplicate id)", () => {
    const first = defineModule("m", { tabs: { a: Board } });
    const second = defineModule("m", { tabs: { a: List } });
    expect(createModuleUiRegistry([first, second]).m).toBe(second);
  });

  it("createModuleUiRegistry tolerates an omitted list", () => {
    expect(createModuleUiRegistry()).toEqual({});
  });

  it("resolveTabComponent returns the registered component for a known tab", () => {
    const registry = createModuleUiRegistry([
      defineModule("finance", { tabs: { transactions: Board } }),
    ]);
    expect(resolveTabComponent(registry, "finance", "transactions")).toBe(Board);
  });

  it("resolveTabComponent returns undefined for an unknown tab, unknown module, or missing id", () => {
    const registry = createModuleUiRegistry([
      defineModule("finance", { tabs: { transactions: Board } }),
    ]);
    expect(resolveTabComponent(registry, "finance", "budgets")).toBeUndefined();
    expect(resolveTabComponent(registry, "nutrition", "transactions")).toBeUndefined();
    expect(resolveTabComponent(registry, undefined, "transactions")).toBeUndefined();
  });
});
