import { createContext, useContext } from "react";
import type { Module } from "./api";

export interface ActiveModuleContextValue {
  modules: Module[];
  activeModule: Module | undefined;
  activeModuleId: string | undefined;
  setActiveModuleId: (id: string) => void;
}

/**
 * The module a path belongs to, or `undefined` for module-agnostic paths (e.g. `/chat`, which is
 * shared across modules). Matches a tab's exact `route`; the always-present chat tab is ignored.
 */
export function moduleIdForPath(
  modules: Module[] | undefined,
  pathname: string,
): string | undefined {
  return modules?.find((m) =>
    m.tabs.some((t) => t.id !== "chat" && t.route === pathname),
  )?.id;
}

/**
 * The active module id. Resolved from, in priority order:
 *   1. the module the current path belongs to — the URL is the source of truth when it names a
 *      module, which is what makes a deep-linked/refreshed tab route work;
 *   2. the module the user explicitly picked in the switcher (remembered across module-agnostic
 *      routes like `/chat`);
 *   3. the first module in the manifest.
 *
 * Deriving this every render (rather than defaulting via an effect) is what prevents the shell's
 * catch-all redirect from bouncing a deep link to `/chat` before the manifest has loaded.
 */
export function resolveActiveModuleId(
  modules: Module[] | undefined,
  selectedModuleId: string | undefined,
  pathname: string,
): string | undefined {
  return moduleIdForPath(modules, pathname) ?? selectedModuleId ?? modules?.[0]?.id;
}

export const ActiveModuleContext = createContext<
  ActiveModuleContextValue | undefined
>(undefined);

export function useActiveModule(): ActiveModuleContextValue {
  const ctx = useContext(ActiveModuleContext);
  if (!ctx) {
    throw new Error("useActiveModule must be used within ActiveModuleProvider");
  }
  return ctx;
}
