import { useEffect, useState } from "react";

/**
 * Tracks a CSS media query, re-rendering when it flips (resize, rotation). Environments without
 * `matchMedia` (jsdom, SSR) read as non-matching — components fall back to their wide layout,
 * which is also what keeps the existing desktop-shaped tests honest.
 */
export function useMediaQuery(query: string): boolean {
  const [matches, setMatches] = useState(
    () => typeof window !== "undefined" && typeof window.matchMedia === "function" && window.matchMedia(query).matches,
  );

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return;
    const mql = window.matchMedia(query);
    const onChange = (e: MediaQueryListEvent) => setMatches(e.matches);
    setMatches(mql.matches);
    mql.addEventListener("change", onChange);
    return () => mql.removeEventListener("change", onChange);
  }, [query]);

  return matches;
}

/** Below Tailwind's `md` (768px) — the same threshold the sidebar collapses into its drawer at. */
export const NARROW_QUERY = "(max-width: 767px)";
