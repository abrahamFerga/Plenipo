import { NavLink } from "react-router-dom";
import { useMe } from "../hooks/useMe";
import { useBranding } from "../lib/branding";
import { ModuleSwitcher } from "./ModuleSwitcher";
import { NotificationBell } from "./NotificationBell";
import { ThemeToggle } from "./ThemeToggle";

/** True if the caller holds any platform-administration permission (or the global wildcard). */
function canAdminister(permissions: string[]): boolean {
  return permissions.some(
    (p) => p === "*" || p === "platform.*" || p.startsWith("platform."),
  );
}

// The admin console is a separate app (served at /admin), not a route inside this shell — so the
// link is a real navigation, not a router NavLink. Override with VITE_ADMIN_URL when the console
// is hosted elsewhere.
const ADMIN_URL = import.meta.env.VITE_ADMIN_URL ?? "/admin";

interface TopBarProps {
  /** Toggles the mobile navigation drawer. When omitted, the hamburger button is not rendered. */
  onToggleSidebar?: () => void;
  /** Whether the mobile drawer is open — drives the hamburger's aria-expanded. */
  sidebarOpen?: boolean;
}

export function TopBar({ onToggleSidebar, sidebarOpen = false }: TopBarProps = {}) {
  const { data: me } = useMe();
  const { name = "Plenipo", logo } = useBranding();
  const showAdmin = canAdminister(me?.permissions ?? []);

  return (
    <header className="flex h-14 items-center gap-3 border-b border-slate-200 bg-white px-4 sm:gap-6 dark:border-slate-700 dark:bg-slate-900">
      {onToggleSidebar && (
        <button
          type="button"
          onClick={onToggleSidebar}
          aria-label="Open navigation"
          aria-expanded={sidebarOpen}
          className="focus-ring rounded-md p-1.5 text-slate-600 hover:bg-slate-100 md:hidden dark:text-slate-300 dark:hover:bg-slate-800"
        >
          <svg
            viewBox="0 0 20 20"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            className="h-5 w-5"
            aria-hidden="true"
          >
            <path d="M3 5h14M3 10h14M3 15h14" />
          </svg>
        </button>
      )}
      <div className="flex items-center gap-2">
        {logo ?? (
          <div className="flex h-7 w-7 items-center justify-center rounded-md bg-brand-600 text-sm font-bold text-white">
            C
          </div>
        )}
        <span className="hidden text-lg font-semibold tracking-tight text-slate-900 sm:inline dark:text-slate-100">
          {name}
        </span>
      </div>

      <ModuleSwitcher />

      <nav aria-label="Primary" className="flex items-center gap-1 text-sm">
        <NavLink
          to="/chat"
          className={({ isActive }) =>
            `focus-ring rounded-md px-3 py-1.5 font-medium ${
              isActive
                ? "bg-slate-100 text-slate-900 dark:bg-slate-800 dark:text-slate-100"
                : "text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100"
            }`
          }
        >
          Workspace
        </NavLink>
        {showAdmin && (
          <a
            href={ADMIN_URL}
            className="focus-ring rounded-md px-3 py-1.5 font-medium text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100"
          >
            Admin ↗
          </a>
        )}
      </nav>

      <div className="ml-auto flex items-center gap-2">
        <NotificationBell />
        <ThemeToggle />
        {/* The user's name doubles as the door to their own settings (connected accounts). */}
        <NavLink
          to="/account/connections"
          title="Connected accounts"
          className={({ isActive }) =>
            `focus-ring hidden rounded-md px-2 py-1 text-sm sm:block ${
              isActive
                ? "bg-slate-100 text-slate-900 dark:bg-slate-800 dark:text-slate-100"
                : "text-slate-600 hover:text-slate-900 dark:text-slate-300 dark:hover:text-slate-100"
            }`
          }
        >
          {me?.displayName ?? "…"}
        </NavLink>
      </div>
    </header>
  );
}
