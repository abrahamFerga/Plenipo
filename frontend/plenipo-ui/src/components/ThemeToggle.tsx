import { useTheme, type ThemePreference } from "../lib/theme";

const NEXT: Record<ThemePreference, ThemePreference> = {
  light: "dark",
  dark: "system",
  system: "light",
};

const LABEL: Record<ThemePreference, string> = {
  light: "Light theme",
  dark: "Dark theme",
  system: "System theme",
};

/**
 * Cycles light → dark → system. "System" follows the OS setting live; the choice persists across
 * sessions. Sits in both app headers, styled to match their icon buttons.
 */
export function ThemeToggle() {
  const { preference, setPreference } = useTheme();

  return (
    <button
      type="button"
      onClick={() => setPreference(NEXT[preference])}
      aria-label={`${LABEL[preference]} — switch to ${LABEL[NEXT[preference]].toLowerCase()}`}
      title={`${LABEL[preference]} (click for ${LABEL[NEXT[preference]].toLowerCase()})`}
      className="focus-ring rounded-md p-1.5 text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
    >
      {preference === "light" ? <SunIcon /> : preference === "dark" ? <MoonIcon /> : <MonitorIcon />}
    </button>
  );
}

function SunIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      className="h-5 w-5"
      aria-hidden="true"
      data-icon="sun"
    >
      <circle cx="10" cy="10" r="3.5" />
      <path d="M10 2v2M10 16v2M2 10h2M16 10h2M4.3 4.3l1.4 1.4M14.3 14.3l1.4 1.4M15.7 4.3l-1.4 1.4M5.7 14.3l-1.4 1.4" />
    </svg>
  );
}

function MoonIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-5 w-5"
      aria-hidden="true"
      data-icon="moon"
    >
      <path d="M16.5 11.5A6.5 6.5 0 0 1 8.5 3.5a6.5 6.5 0 1 0 8 8Z" />
    </svg>
  );
}

function MonitorIcon() {
  return (
    <svg
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      className="h-5 w-5"
      aria-hidden="true"
      data-icon="monitor"
    >
      <rect x="2.5" y="3.5" width="15" height="10" rx="1.5" />
      <path d="M7 16.5h6M10 13.5v3" />
    </svg>
  );
}
