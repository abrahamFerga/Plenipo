import { Suspense, type ComponentType } from "react";
import type { ModuleTab } from "../lib/api";
import type { ModuleTabProps } from "../lib/moduleUi";
import { GenericTab } from "./GenericTab";
import { TabErrorBoundary } from "./TabErrorBoundary";

interface ModuleTabViewProps {
  moduleId: string;
  tab: ModuleTab;
  /** The host-registered renderer for this tab, if any; falls back to {@link GenericTab} when absent. */
  component?: ComponentType<ModuleTabProps>;
}

/**
 * Renders one module tab: a host-registered custom component when the module UI provides one,
 * otherwise the built-in server-driven {@link GenericTab}. This is the single seam where the
 * client-side registry meets the server-declared manifest.
 *
 * Tab content is wrapped so host components are first-class plugins: an error boundary contains a
 * crash to this one tab, and a Suspense boundary lets a host `React.lazy()` its page for code-splitting.
 */
export function ModuleTabView({ moduleId, tab, component: Custom }: ModuleTabViewProps) {
  return (
    <TabErrorBoundary label={tab.label}>
      <Suspense fallback={<p className="text-sm text-slate-500">Loading…</p>}>
        {Custom ? <Custom moduleId={moduleId} tab={tab} /> : <GenericTab tab={tab} />}
      </Suspense>
    </TabErrorBoundary>
  );
}
