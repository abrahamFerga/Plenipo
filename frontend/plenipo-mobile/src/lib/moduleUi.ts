import type { ComponentType } from "react";
import type { ModuleTab } from "@plenipo/client";

/**
 * The product-extension seam, deliberately identical in shape to `@plenipo/ui`'s web registry.
 *
 * The API manifest stays the source of truth for WHICH modules and tabs exist and WHO can see
 * them — that is navigation and RBAC, and it is not the client's business to decide. A product
 * owns only HOW a given tab renders: register a native screen for a `(moduleId, tabId)` pair and
 * it replaces the generic renderer for that tab alone. Everything unregistered keeps working, so
 * a module that needs no custom mobile UI costs zero React Native code.
 */

/** Props every registered tab screen receives. */
export interface ModuleTabProps {
  moduleId: string;
  tab: ModuleTab;
}

/** One module's registered screens, keyed by tab id. */
export interface PlenipoModuleUi {
  moduleId: string;
  tabs: Record<string, ComponentType<ModuleTabProps>>;
}

/**
 * Declare a module's native screens.
 *
 * ```tsx
 * const legal = defineModule("legal", { tabs: { matters: MattersBoard } });
 * <PlenipoMobileApp moduleUi={[legal]} config={…} />
 * ```
 */
export function defineModule(
  moduleId: string,
  ui: { tabs: Record<string, ComponentType<ModuleTabProps>> },
): PlenipoModuleUi {
  return { moduleId, tabs: ui.tabs };
}

export type ModuleUiRegistry = Map<string, PlenipoModuleUi>;

export function createModuleUiRegistry(modules: PlenipoModuleUi[] = []): ModuleUiRegistry {
  // Later registrations win, so a product can override a screen a shared package registered.
  return new Map(modules.map((m) => [m.moduleId, m]));
}

/** The registered screen for a tab, or undefined to fall back to the generic renderer. */
export function resolveTabComponent(
  registry: ModuleUiRegistry | undefined,
  moduleId: string,
  tabId: string,
): ComponentType<ModuleTabProps> | undefined {
  return registry?.get(moduleId)?.tabs[tabId];
}
