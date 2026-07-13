import type { ReactElement } from "react";

/**
 * THE icon map for module-tab manifest icons — extend this file, never fork a second map.
 *
 * `ModuleTab.icon` / `TabDescriptor.Icon` carry lucide-style kebab-case names ("wallet",
 * "pie-chart", …) chosen by module authors; until the bottom navigation bar nothing in the shell
 * consumed them. Glyphs are stroke-drawn on the same 20×20 grid as the shell's other icons
 * (TopBar's hamburger, ThemeToggle) so mixed surfaces stay visually consistent, and the set below
 * covers every name the in-repo sample modules and known consuming products declare today.
 */
const GLYPHS: Record<string, ReactElement> = {
  apple: (
    <path d="M10 6.6C8.8 5.5 7.2 5.1 5.7 5.7 3.6 6.5 2.7 8.7 3.3 11c.7 2.9 2.6 5.5 4.4 5.5.8 0 1.5-.5 2.3-.5s1.5.5 2.3.5c1.8 0 3.7-2.6 4.4-5.5.6-2.3-.3-4.5-2.4-5.3-1.5-.6-3.1-.2-4.3.9zm0 0c-.1-1.7.6-3 2-4.1" />
  ),
  "arrow-left-right": <path d="M6.5 2.5 3 6l3.5 3.5M3 6h14m-3.5 4.5L17 14l-3.5 3.5M17 14H3" />,
  banknote: (
    <>
      <rect x="2" y="5.5" width="16" height="9" rx="1.5" />
      <circle cx="10" cy="10" r="2" />
      <path d="M5.5 10h.01M14.5 10h.01" />
    </>
  ),
  "book-open": (
    <path d="M10 5C8.4 3.8 6.5 3.3 4.5 3.3h-2v12.9h2c2 0 3.9.5 5.5 1.7 1.6-1.2 3.5-1.7 5.5-1.7h2V3.3h-2c-2 0-3.9.5-5.5 1.7zm0 0v12.9" />
  ),
  "calendar-clock": (
    <>
      <rect x="2.5" y="4" width="15" height="13.5" rx="1.5" />
      <path d="M6.5 2v4M13.5 2v4M2.5 8.5h15M10 11v2.5l1.8.9" />
    </>
  ),
  "check-square": (
    <>
      <rect x="3" y="3" width="14" height="14" rx="2" />
      <path d="m6.8 10 2.3 2.3 4.5-4.6" />
    </>
  ),
  "credit-card": (
    <>
      <rect x="2" y="4.5" width="16" height="11" rx="1.5" />
      <path d="M2 8.5h16" />
    </>
  ),
  "file-text": (
    <path d="M11.5 2.5h-6A1.5 1.5 0 0 0 4 4v12a1.5 1.5 0 0 0 1.5 1.5h9A1.5 1.5 0 0 0 16 16V7zm0 0V7H16M7 10.5h6M7 13.5h6" />
  ),
  flag: (
    <path d="M4.5 17.5v-15c1.2-.6 2.4-.9 3.7-.9 2.5 0 4 1.4 6.1 1.4.8 0 1.5-.1 2.2-.4v8.6c-.7.3-1.4.4-2.2.4-2.1 0-3.6-1.4-6.1-1.4-1.3 0-2.5.3-3.7.9" />
  ),
  folder: (
    <path d="M2.5 5A1.5 1.5 0 0 1 4 3.5h4.2L10 6h6a1.5 1.5 0 0 1 1.5 1.5V15a1.5 1.5 0 0 1-1.5 1.5H4A1.5 1.5 0 0 1 2.5 15z" />
  ),
  landmark: <path d="M2.5 17.5h15M4.5 14.5v-6m3.6 6v-6m3.8 6v-6m3.6 6v-6M2.5 8.5 10 3l7.5 5.5z" />,
  "layout-dashboard": (
    <>
      <rect x="2.5" y="2.5" width="6.5" height="8" rx="1" />
      <rect x="11" y="2.5" width="6.5" height="5" rx="1" />
      <rect x="11" y="9.5" width="6.5" height="8" rx="1" />
      <rect x="2.5" y="12.5" width="6.5" height="5" rx="1" />
    </>
  ),
  // Zero-length segments + round linecaps render as the leading bullet dots.
  list: <path d="M7 5h10.5M7 10h10.5M7 15h10.5M3 5h.01M3 10h.01M3 15h.01" />,
  "list-checks": (
    <path d="m2.5 4.5 1.6 1.6L7 3.2M2.5 10.5l1.6 1.6L7 9.2M10 5.5h7.5M10 11.5h7.5M10 16.5h7.5" />
  ),
  "message-circle": <path d="M6.6 16.7A7.5 7.5 0 1 0 3.3 13.4L1.7 18.3z" />,
  "pie-chart": (
    <>
      <circle cx="10" cy="10" r="7.5" />
      <path d="M10 2.5V10l5.3 5.3" />
    </>
  ),
  receipt: <path d="M4 2.5h12v15l-2-1.5-2 1.5-2-1.5-2 1.5-2-1.5-2 1.5zM7 6.5h6M7 9.5h6M7 12.5h3" />,
  repeat: (
    <path d="m14 2.5 3 3-3 3M17 5.5H5.5A2.5 2.5 0 0 0 3 8v.5m3 9-3-3 3-3m-3 3h11.5A2.5 2.5 0 0 0 17 12v-.5" />
  ),
  salad: (
    <>
      <path d="M2.5 10.5a7.5 6.5 0 0 0 15 0z" />
      <path d="M7 10.5C7 6.5 8.5 4 11 2.5c.5 2 .2 4.5-1.5 6" />
    </>
  ),
  scale: (
    <>
      <path d="M10 3.5v13M6.5 16.5h7M2 6.5h16" />
      <path d="m4.5 6.5-2 4.5h4zm11 0-2 4.5h4z" />
    </>
  ),
  settings: (
    <>
      <circle cx="10" cy="10" r="2.8" />
      <path d="M10 2.2v2.2M10 15.6v2.2M2.2 10h2.2M15.6 10h2.2M4.5 4.5l1.6 1.6M13.9 13.9l1.6 1.6M15.5 4.5l-1.6 1.6M6.1 13.9l-1.6 1.6" />
    </>
  ),
  "shield-check": (
    <path d="M10 2.5 4 4.8v5c0 4 2.6 6.6 6 7.7 3.4-1.1 6-3.7 6-7.7v-5zM7.2 9.8l1.9 1.9 3.7-3.8" />
  ),
  tags: (
    <>
      <path d="M3 3h6.4a1.5 1.5 0 0 1 1 .4l6.2 6.2a1.5 1.5 0 0 1 0 2.1l-4.9 4.9a1.5 1.5 0 0 1-2.1 0L3.4 10.4a1.5 1.5 0 0 1-.4-1z" />
      <path d="M6.5 6.5h.01" />
    </>
  ),
  target: (
    <>
      <circle cx="10" cy="10" r="7.5" />
      <circle cx="10" cy="10" r="4.5" />
      <circle cx="10" cy="10" r="1.5" />
    </>
  ),
  timer: (
    <>
      <circle cx="10" cy="11.5" r="6" />
      <path d="M10 8.5v3M8 2.5h4M10 2.5v3" />
    </>
  ),
  "trending-up": <path d="M2.5 14.5 8 9l3.5 3.5 6-6M13 6.5h4.5V11" />,
  users: (
    <>
      <circle cx="7" cy="6.5" r="3" />
      <path d="M2 17.5c0-2.8 2.2-5 5-5s5 2.2 5 5M13.3 3.9a3 3 0 0 1 0 5.2M15.5 12.9c1.5.8 2.5 2.6 2.5 4.6" />
    </>
  ),
  wallet: (
    <>
      <path d="M2.5 6A2.5 2.5 0 0 1 5 3.5h10A2.5 2.5 0 0 1 17.5 6v8a2.5 2.5 0 0 1-2.5 2.5H5A2.5 2.5 0 0 1 2.5 14z" />
      <path d="M17.5 8.5H14a2 2 0 0 0 0 4h3.5" />
    </>
  ),
};

/**
 * Tabs with no declared icon — and names we haven't drawn yet — fall back to a neutral circle-dot
 * so every nav item keeps an even, legible shape (never a blank gap or a broken glyph).
 */
const FALLBACK: ReactElement = (
  <>
    <circle cx="10" cy="10" r="7" />
    <path d="M10 10h.01" />
  </>
);

interface TabIconProps {
  /** The manifest icon name (`ModuleTab.icon`); undefined and unknown names use the fallback. */
  name?: string;
  /** Sizing classes from the caller (h-5 w-5 fits nav rows). */
  className?: string;
}

/** A module tab's manifest icon as a stroke-drawn glyph, matching the shell's other icons. */
export function TabIcon({ name, className }: TabIconProps) {
  const known = name !== undefined && name in GLYPHS;
  return (
    <svg
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      data-icon={known ? name : "fallback"}
      aria-hidden="true"
    >
      {known ? GLYPHS[name] : FALLBACK}
    </svg>
  );
}
