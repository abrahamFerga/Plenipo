import { useEffect } from "react";
import { NavLink } from "react-router-dom";
import type { ModuleTab } from "../lib/api";

interface SidebarProps {
  moduleId: string | undefined;
  tabs: ModuleTab[];
  /** Whether the mobile drawer is open. Ignored at md+, where the sidebar is always visible. */
  open?: boolean;
  /** Closes the mobile drawer (backdrop click or Escape). */
  onClose?: () => void;
  /** Invoked when a nav link is clicked — lets the shell auto-close the mobile drawer. */
  onNavigate?: () => void;
}

function navClass({ isActive }: { isActive: boolean }): string {
  const base =
    "focus-ring block rounded-md px-3 py-2 text-sm font-medium transition-colors";
  return isActive
    ? `${base} bg-brand-600 text-white`
    : `${base} text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800`;
}

/** The nav content shared by the static (desktop) sidebar and the mobile drawer. */
function SidebarNav({
  moduleId,
  tabs,
  onNavigate,
  className,
}: {
  moduleId: string | undefined;
  tabs: ModuleTab[];
  onNavigate?: () => void;
  className: string;
}) {
  return (
    <nav aria-label="Module tabs" className={className}>
      {moduleId && (
        <p className="mb-2 px-3 text-xs font-semibold uppercase tracking-wide text-slate-400">
          {moduleId}
        </p>
      )}
      <ul className="space-y-1">
        {tabs.map((tab) => (
          <li key={tab.id}>
            <NavLink to={tab.route} end className={navClass} onClick={onNavigate}>
              {tab.label}
            </NavLink>
          </li>
        ))}
      </ul>
    </nav>
  );
}

export function Sidebar({ moduleId, tabs, open = false, onClose, onNavigate }: SidebarProps) {
  // Close the mobile drawer on Escape so keyboard users aren't trapped behind the backdrop.
  useEffect(() => {
    if (!open) return;
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") onClose?.();
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, onClose]);

  return (
    <>
      {/* Desktop: static sidebar, always visible at md+. */}
      <SidebarNav
        moduleId={moduleId}
        tabs={tabs}
        className="hidden w-56 shrink-0 border-r border-slate-200 bg-white p-3 md:block dark:border-slate-700 dark:bg-slate-900"
      />
      {/* Mobile: drawer over a backdrop, toggled from the top bar's hamburger. */}
      {open && (
        <>
          <button
            type="button"
            aria-label="Close navigation"
            onClick={onClose}
            className="focus-ring fixed inset-0 z-30 bg-black/40 md:hidden"
          />
          <SidebarNav
            moduleId={moduleId}
            tabs={tabs}
            onNavigate={onNavigate}
            className="fixed inset-y-0 left-0 z-40 w-64 border-r border-slate-200 bg-white p-3 shadow-xl md:hidden dark:border-slate-700 dark:bg-slate-900"
          />
        </>
      )}
    </>
  );
}
