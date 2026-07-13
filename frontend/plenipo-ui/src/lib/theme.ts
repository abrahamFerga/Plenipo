import { useSyncExternalStore } from "react";

/**
 * Color-scheme preference: explicit light/dark, or "system" to follow the OS setting live.
 * The resolved scheme is applied as a `dark` class on <html> (Tailwind `darkMode: "selector"`),
 * and the preference persists in localStorage. Both apps also run a tiny inline script in
 * index.html that applies the stored preference before the bundle loads, so a dark-mode reload
 * never flashes light.
 */
export type ThemePreference = "light" | "dark" | "system";

const STORAGE_KEY = "plenipo.theme";

let preference: ThemePreference = "system";
const listeners = new Set<() => void>();

function systemPrefersDark(): boolean {
  return typeof window !== "undefined" && !!window.matchMedia?.("(prefers-color-scheme: dark)").matches;
}

/** The scheme a preference lands on ("system" resolves against the OS setting). */
export function resolveTheme(pref: ThemePreference): "light" | "dark" {
  return pref === "system" ? (systemPrefersDark() ? "dark" : "light") : pref;
}

function apply(pref: ThemePreference) {
  document.documentElement.classList.toggle("dark", resolveTheme(pref) === "dark");
}

/** Reads the stored preference, applies it, and starts following OS changes. Call once at startup. */
export function initTheme(): void {
  const stored = localStorage.getItem(STORAGE_KEY);
  preference = stored === "light" || stored === "dark" || stored === "system" ? stored : "system";
  apply(preference);

  // Follow the OS live while (and only while) the preference is "system".
  window.matchMedia?.("(prefers-color-scheme: dark)").addEventListener?.("change", () => {
    if (preference === "system") {
      apply(preference);
      listeners.forEach((l) => l());
    }
  });
}

export function getThemePreference(): ThemePreference {
  return preference;
}

export function setThemePreference(pref: ThemePreference): void {
  preference = pref;
  localStorage.setItem(STORAGE_KEY, pref);
  apply(pref);
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The current preference plus a setter — the ThemeToggle's (or any custom UI's) hook. */
export function useTheme(): {
  preference: ThemePreference;
  resolved: "light" | "dark";
  setPreference: (pref: ThemePreference) => void;
} {
  const pref = useSyncExternalStore(subscribe, getThemePreference);
  return { preference: pref, resolved: resolveTheme(pref), setPreference: setThemePreference };
}
