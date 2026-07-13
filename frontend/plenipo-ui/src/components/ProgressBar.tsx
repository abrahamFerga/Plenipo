import { formatY } from "../lib/chartTheme";

/**
 * A value measured against a target (spent vs. budget, funded vs. goal) with semantic banding:
 * healthy under `warnAt`, warning from `warnAt` to the target, critical past it. Status is
 * never color alone — every band pairs its color with an icon and explicit text ("Over by 40",
 * not just a red bar), so the reading survives colorblindness, grayscale, and dark mode alike.
 * The track caps at 100%; overshoot is stated by the text, not drawn off the edge.
 *
 * Like {@link StatTile}, a composition piece for product dashboards and custom tabs — not a
 * layout. Numbers format through the shared chart vocabulary; pass `text` when the caller owns
 * the phrasing (e.g. currency-qualified amounts).
 */
export interface ProgressBarProps {
  /** How far along the measure is (e.g. amount spent). */
  value: number;
  /** The target the value reads against (e.g. the budget). Must be positive. */
  max: number;
  /** What is being measured (e.g. "Groceries"). */
  label: string;
  /** Fraction of `max` where healthy turns to warning. Default 0.85. */
  warnAt?: number;
  /** Explicit status text; when absent: "N left" under the target, "Over by N" past it. */
  text?: string;
}

type Band = "healthy" | "warning" | "critical";

const BAND_BAR: Record<Band, string> = {
  healthy: "bg-emerald-600 dark:bg-emerald-500",
  warning: "bg-amber-500 dark:bg-amber-400",
  critical: "bg-red-600 dark:bg-red-500",
};

const BAND_TEXT: Record<Band, string> = {
  healthy: "text-emerald-700 dark:text-emerald-400",
  warning: "text-amber-700 dark:text-amber-400",
  critical: "text-red-700 dark:text-red-400",
};

/** Redundant per-band icons (check / exclamation triangle / alert circle), stroke-drawn like the shell's other glyphs. */
function BandIcon({ band }: { band: Band }) {
  const common = {
    viewBox: "0 0 20 20",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.8,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    className: "h-3.5 w-3.5 shrink-0",
    "aria-hidden": true,
  };
  if (band === "healthy") {
    return (
      <svg {...common}>
        <path d="m4.5 10.5 3.5 3.5 7.5-8" />
      </svg>
    );
  }
  if (band === "warning") {
    return (
      <svg {...common}>
        <path d="M10 3 2.5 16h15L10 3Z" />
        <path d="M10 8v4" />
        <path d="M10 14.5v.01" />
      </svg>
    );
  }
  return (
    <svg {...common}>
      <circle cx="10" cy="10" r="7.5" />
      <path d="M10 6.5v4" />
      <path d="M10 13.5v.01" />
    </svg>
  );
}

export function ProgressBar({ value, max, label, warnAt = 0.85, text }: ProgressBarProps) {
  const ratio = max > 0 ? value / max : 0;
  const band: Band = ratio > 1 ? "critical" : ratio >= warnAt ? "warning" : "healthy";
  const statusText = text ?? (ratio > 1 ? `Over by ${formatY(value - max)}` : `${formatY(max - value)} left`);

  return (
    <div>
      <div className="mb-1 flex items-baseline justify-between gap-4 text-sm">
        <span className="min-w-0 truncate font-medium text-slate-700 dark:text-slate-200">{label}</span>
        <span className={`inline-flex shrink-0 items-center gap-1 text-xs font-medium ${BAND_TEXT[band]}`} data-testid="progress-status">
          <BandIcon band={band} />
          {statusText}
        </span>
      </div>
      <div
        role="progressbar"
        aria-label={label}
        aria-valuenow={Math.round(Math.min(ratio, 1) * 100)}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuetext={`${formatY(value)} of ${formatY(max)} — ${statusText}`}
        className="h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-700"
      >
        <div
          data-testid="progress-fill"
          className={`h-full rounded-full ${BAND_BAR[band]}`}
          style={{ width: `${Math.min(ratio, 1) * 100}%` }}
        />
      </div>
    </div>
  );
}
