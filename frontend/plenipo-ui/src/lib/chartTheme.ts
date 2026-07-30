// Shared visual vocabulary for the server-driven chart kinds (line / donut / bar), so every
// geometry draws from the same validated categorical palette and number formatting.
//
// Light/dark are separately selected steps, both validated against the app's surfaces — see the
// palette notes in the PR that introduced TabChart. Assigned in fixed order, never cycled: a
// 5th series/segment folds into an "Other"/"more" note rather than inventing a hue. Classes are
// static strings so Tailwind's JIT sees them whole.

export const SERIES_STROKE = [
  "stroke-[#2a78d6] dark:stroke-[#3987e5]",
  "stroke-[#1baf7a] dark:stroke-[#199e70]",
  "stroke-[#eda100] dark:stroke-[#c98500]",
  "stroke-[#4a3aa7] dark:stroke-[#9085e9]",
];
export const SERIES_FILL = [
  "fill-[#2a78d6] dark:fill-[#3987e5]",
  "fill-[#1baf7a] dark:fill-[#199e70]",
  "fill-[#eda100] dark:fill-[#c98500]",
  "fill-[#4a3aa7] dark:fill-[#9085e9]",
];
export const SERIES_BG = [
  "bg-[#2a78d6] dark:bg-[#3987e5]",
  "bg-[#1baf7a] dark:bg-[#199e70]",
  "bg-[#eda100] dark:bg-[#c98500]",
  "bg-[#4a3aa7] dark:bg-[#9085e9]",
];
// The cap itself lives with the shaping in @plenipo/client, so the web and mobile shells roll up
// the same tail. The palettes above must offer exactly that many hues — pinned in chartTheme.test.ts.
export { MAX_SERIES } from "@plenipo/client";

// The rolled-up tail ("Other") is recessive by design — identity lives in the named segments.
export const OTHER_STROKE = "stroke-slate-400 dark:stroke-slate-500";
export const OTHER_FILL = "fill-slate-400 dark:fill-slate-500";
export const OTHER_BG = "bg-slate-400 dark:bg-slate-500";

// Number formatting and tick selection are shaping, not styling — shared with every other shell.
export { formatY, niceTicks } from "@plenipo/client";
