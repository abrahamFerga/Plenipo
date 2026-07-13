import { useParams } from "react-router-dom";
import { GenericTab } from "@plenipo/ui";
import { useAdminExtensions } from "./extensions";

/**
 * The route target for /ext/:moduleId/:tabId — resolves the module-declared admin tab and renders
 * it through the platform's generic tab machinery (which brings the table, editor, chart, and
 * actions, including the tab's own heading).
 */
export function ExtensionPage() {
  const { moduleId, tabId } = useParams();
  const extensions = useAdminExtensions();

  if (extensions.isLoading) {
    return <p className="text-sm text-slate-500">Loading…</p>;
  }
  if (extensions.isError) {
    return <p className="text-sm text-red-600">{(extensions.error as Error).message}</p>;
  }

  const module = extensions.data?.find((m) => m.id === moduleId);
  const tab = module?.tabs.find((t) => t.id === tabId);
  if (!module || !tab) {
    return (
      <p className="text-sm text-slate-500">
        This module page doesn&apos;t exist here — or your role can&apos;t see it.
      </p>
    );
  }

  return (
    <div className="space-y-1">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">
        {module.displayName}
      </p>
      <GenericTab tab={tab} />
    </div>
  );
}
