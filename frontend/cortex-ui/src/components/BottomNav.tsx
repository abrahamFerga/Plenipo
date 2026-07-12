import { NavLink } from "react-router-dom";
import type { ModuleTab } from "../lib/api";
import { NARROW_QUERY, useMediaQuery } from "../hooks/useMediaQuery";
import { TabIcon } from "./TabIcon";

/**
 * Five items is the most that stay comfortably tappable on a 320–430px viewport. With more tabs
 * than that, the fifth slot becomes a fixed "More" button opening the drawer; with five or fewer,
 * every tab fits and "More" would be a dead button, so it's dropped.
 */
const MAX_ITEMS = 5;

interface BottomNavProps {
  /** The same array (and order) the sidebar renders: Chat first when enabled, then module tabs. */
  tabs: ModuleTab[];
  /** Opens the shell's existing navigation drawer — the overflow surface for the remaining tabs. */
  onMore: () => void;
  /** Whether the drawer is open — drives the More button's aria-expanded. */
  moreOpen?: boolean;
}

/**
 * Icon + label, both always visible. The active item is never color-only: NavLink adds
 * `aria-current="page"`, the weight jumps to semibold, and a top indicator bar appears — the brand
 * color rides along as the third cue, not the only one.
 */
const itemBase =
  "focus-ring relative flex min-h-14 w-full flex-col items-center justify-center gap-1 px-1 text-[11px] leading-tight transition-colors";

function itemClass({ isActive }: { isActive: boolean }): string {
  return isActive
    ? `${itemBase} font-semibold text-brand-600 dark:text-brand-400`
    : `${itemBase} font-medium text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-slate-100`;
}

/**
 * Mobile primary navigation: a fixed bottom bar with the first destinations of the shell's tab
 * list, replacing the drawer as the first-tap surface below `md` (the drawer stays for overflow).
 *
 * Rendering is gated on the same `NARROW_QUERY` the drawer and `GenericTab`'s card-mode use, so
 * desktop keeps today's DOM byte-for-byte and jsdom (no matchMedia → non-matching) naturally pins
 * the bar's absence in desktop-shaped tests. `md:hidden` stays on as a CSS backstop for the
 * moment between a viewport crossing the breakpoint and React re-rendering.
 */
export function BottomNav({ tabs, onMore, moreOpen = false }: BottomNavProps) {
  const narrow = useMediaQuery(NARROW_QUERY);
  if (!narrow || tabs.length === 0) return null;

  const overflowing = tabs.length > MAX_ITEMS;
  const visible = overflowing ? tabs.slice(0, MAX_ITEMS - 1) : tabs;

  return (
    // z-20 keeps the bar under the drawer's backdrop (z-30), so an open drawer covers it and a
    // tap "on the bar" closes the drawer instead of navigating blind. pb-[env(...)] keeps the
    // items above the iOS home indicator (needs viewport-fit=cover in the host page).
    <nav
      aria-label="Tab bar"
      className="fixed inset-x-0 bottom-0 z-20 border-t border-slate-200 bg-white pb-[env(safe-area-inset-bottom)] md:hidden dark:border-slate-700 dark:bg-slate-900"
    >
      <ul className="flex items-stretch">
        {visible.map((tab) => (
          <li key={tab.id} className="min-w-0 flex-1">
            <NavLink to={tab.route} end className={itemClass}>
              {({ isActive }) => (
                <>
                  {isActive && (
                    <span
                      data-active-indicator
                      aria-hidden="true"
                      className="absolute inset-x-4 top-0 h-0.5 rounded-b-full bg-brand-600 dark:bg-brand-400"
                    />
                  )}
                  <TabIcon name={tab.icon} className="h-5 w-5" />
                  <span className="max-w-full truncate">{tab.label}</span>
                </>
              )}
            </NavLink>
          </li>
        ))}
        {overflowing && (
          <li className="min-w-0 flex-1">
            <button
              type="button"
              onClick={onMore}
              aria-expanded={moreOpen}
              className={itemClass({ isActive: false })}
            >
              <MoreGlyph />
              <span className="max-w-full truncate">More</span>
            </button>
          </li>
        )}
      </ul>
    </nav>
  );
}

/** A horizontal ellipsis — filled dots read better at this size than stroked ones. */
function MoreGlyph() {
  return (
    <svg viewBox="0 0 20 20" fill="currentColor" className="h-5 w-5" data-icon="more" aria-hidden="true">
      <circle cx="4.5" cy="10" r="1.6" />
      <circle cx="10" cy="10" r="1.6" />
      <circle cx="15.5" cy="10" r="1.6" />
    </svg>
  );
}
