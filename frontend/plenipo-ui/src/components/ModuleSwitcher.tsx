import { useActiveModule } from "../lib/activeModule";

export function ModuleSwitcher() {
  const { modules, activeModuleId, setActiveModuleId } = useActiveModule();

  if (modules.length === 0) {
    return null;
  }

  return (
    <label className="flex items-center gap-2 text-sm">
      <span className="text-slate-500 dark:text-slate-400">Module</span>
      <select
        className="rounded-md border border-slate-300 bg-white px-2 py-1 text-sm text-slate-900 shadow-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 dark:border-slate-600 dark:bg-slate-800 dark:text-slate-100"
        value={activeModuleId ?? ""}
        onChange={(e) => setActiveModuleId(e.target.value)}
      >
        {modules.map((m) => (
          <option key={m.id} value={m.id}>
            {m.displayName}
          </option>
        ))}
      </select>
    </label>
  );
}
