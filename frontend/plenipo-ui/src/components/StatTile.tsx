import type { ReactNode } from "react";
import { formatY } from "../lib/chartTheme";

/**
 * A dashboard stat: one labeled number, read at a glance. Optional garnishes — an icon slot on
 * the left, a caption underneath, a trend sparkline on the right — stay recessive; the value is
 * the point. Numbers format through the shared chart vocabulary (`formatY`) so a tile and the
 * chart next to it never disagree about how 12400 is written; pass a pre-formatted string
 * (e.g. "$12,400") when the caller owns the formatting.
 *
 * This is a composition piece for a product's own dashboard (registered via the moduleUi seam),
 * deliberately not a layout: arrange tiles with your own grid.
 */
export interface StatTileProps {
  /** What the number is (e.g. "Net worth", "Safe to spend"). */
  label: string;
  /** The number itself — numbers are formatted, strings shown verbatim. */
  value: number | string;
  /** Optional secondary line under the value (e.g. "as of today", "3 accounts"). */
  caption?: string;
  /** Optional leading icon; sized by the caller (h-5 w-5 works well). */
  icon?: ReactNode;
  /**
   * Optional trend values, oldest first — rendered as a small sparkline next to the value.
   * Fewer than two points renders nothing (a single point has no direction to show).
   */
  trend?: number[];
}

const SPARK_W = 96;
const SPARK_H = 28;
const SPARK_PAD = 2;

function sparklinePath(trend: number[]): string {
  const min = Math.min(...trend);
  const max = Math.max(...trend);
  const spread = max - min || 1;
  const step = (SPARK_W - SPARK_PAD * 2) / (trend.length - 1);
  return trend
    .map((v, i) => {
      const x = SPARK_PAD + i * step;
      const y = SPARK_PAD + (SPARK_H - SPARK_PAD * 2) * (1 - (v - min) / spread);
      return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");
}

export function StatTile({ label, value, caption, icon, trend }: StatTileProps) {
  const display = typeof value === "number" ? formatY(value) : value;
  const spark = trend && trend.length >= 2 ? sparklinePath(trend) : null;

  return (
    <div className="rounded-lg border border-slate-200 bg-white p-4 dark:border-slate-700 dark:bg-slate-900">
      <div className="flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
        {icon && (
          <span aria-hidden className="shrink-0">
            {icon}
          </span>
        )}
        <span>{label}</span>
      </div>
      <div className="mt-1 flex items-end justify-between gap-4">
        <span className="text-2xl font-semibold tabular-nums text-slate-900 dark:text-slate-100">{display}</span>
        {spark && (
          <svg
            viewBox={`0 0 ${SPARK_W} ${SPARK_H}`}
            className="h-7 w-24 shrink-0"
            role="img"
            aria-label={`${label} trend`}
            data-testid="stat-sparkline"
          >
            <path d={spark} fill="none" strokeWidth="1.5" strokeLinejoin="round" className="stroke-brand-600" />
          </svg>
        )}
      </div>
      {caption && <p className="mt-1 text-xs text-slate-400 dark:text-slate-500">{caption}</p>}
    </div>
  );
}
