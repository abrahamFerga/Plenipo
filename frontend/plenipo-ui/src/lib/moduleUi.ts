import type { ComponentType } from "react";
import type { ModuleTab } from "./api";

// ─────────────────────────────────────────────────────────────────────────────
// Client-side module UI registry.
//
// The server owns *which* modules and tabs exist, and *who* may see them
// (navigation + RBAC, from GET /api/platform/modules). A host application owns
// *how* a tab renders: it registers its own React components here, keyed by tab
// id. A tab with no registered component falls back to the built-in, server-driven
// GenericTab — so low-effort modules cost zero React, and rich modules opt in.
// ─────────────────────────────────────────────────────────────────────────────

/** Props every host-provided tab component receives. */
export interface ModuleTabProps {
  /** The module the tab belongs to (its manifest id). */
  moduleId: string;
  /** The server-declared tab this component is rendering. */
  tab: ModuleTab;
}

/** A host's UI contributions for one module. */
export interface PlenipoModuleUi {
  /** Manifest id of the module these components belong to (matches the server's module id). */
  id: string;
  /**
   * Custom renderer per tab id. A tab id present here is rendered by the given component;
   * any other tab falls back to {@link GenericTab}. The keys are tab ids, not routes.
   */
  tabs?: Record<string, ComponentType<ModuleTabProps>>;
}

/**
 * Declare a host module's UI contributions. Pass the results to `AppShell`/`PlenipoApp`
 * via the `modules` prop.
 *
 * @example
 * const finance = defineModule("finance", {
 *   tabs: { transactions: TransactionsBoard, budgets: BudgetPlanner },
 * });
 * // <PlenipoApp modules={[finance]} />
 */
export function defineModule(
  id: string,
  ui: Omit<PlenipoModuleUi, "id"> = {},
): PlenipoModuleUi {
  return { id, ...ui };
}

/** Fast lookup of module id → its UI contributions (last entry wins on a duplicate id). */
export type ModuleUiRegistry = Record<string, PlenipoModuleUi>;

/** Build a {@link ModuleUiRegistry} from a list of module UIs. */
export function createModuleUiRegistry(
  modules: readonly PlenipoModuleUi[] = [],
): ModuleUiRegistry {
  return Object.fromEntries(modules.map((m) => [m.id, m]));
}

/**
 * Resolve the host-registered component for a tab, or `undefined` to signal a
 * fallback to the generic renderer.
 */
export function resolveTabComponent(
  registry: ModuleUiRegistry,
  moduleId: string | undefined,
  tabId: string,
): ComponentType<ModuleTabProps> | undefined {
  if (!moduleId) return undefined;
  return registry[moduleId]?.tabs?.[tabId];
}
