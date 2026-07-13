import { useMemo } from "react";
import type { TabChart as TabChartSpec } from "../lib/api";
import { formatY, MAX_SERIES, OTHER_BG, OTHER_STROKE, SERIES_BG, SERIES_STROKE } from "../lib/chartTheme";

/**
 * The shell's proportional donut — a tab declaring `chart.kind = "donut"` renders its
 * dataEndpoint rows here. Each row contributes `yField` to the segment named by its `xField`
 * value (rows sharing a label are summed); segments beyond the palette roll into a recessive
 * "Other". The legend carries value + share directly — identity is never hover-only. The total
 * sits in the hole, so the one number a proportional view implies is stated, not implied.
 */

interface Slice {
  label: string;
  value: number;
  isOther: boolean;
}

const SIZE = 160;
const CENTER = SIZE / 2;
const RADIUS = 58;
const RING = 26;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

function buildSlices(rows: Record<string, unknown>[], spec: TabChartSpec): Slice[] {
  const totals = new Map<string, number>();
  for (const row of rows) {
    const value = typeof row[spec.yField] === "number" ? (row[spec.yField] as number) : Number(row[spec.yField]);
    // A share must be a positive, finite number — anything else can't occupy arc length.
    if (!Number.isFinite(value) || value <= 0) continue;
    const label = String(row[spec.xField] ?? "");
    totals.set(label, (totals.get(label) ?? 0) + value);
  }
  const named = [...totals.entries()]
    .map(([label, value]) => ({ label, value, isOther: false }))
    .sort((a, b) => b.value - a.value);
  if (named.length <= MAX_SERIES) return named;
  const other = named.slice(MAX_SERIES).reduce((sum, s) => sum + s.value, 0);
  return [...named.slice(0, MAX_SERIES), { label: "Other", value: other, isOther: true }];
}

export function TabDonutChart({ rows, spec }: { rows: Record<string, unknown>[]; spec: TabChartSpec }) {
  const slices = useMemo(() => buildSlices(rows, spec), [rows, spec]);

  if (slices.length === 0) {
    return (
      <div className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
        No data points yet — the breakdown appears once data accumulates.
      </div>
    );
  }

  const total = slices.reduce((sum, s) => sum + s.value, 0);

  // Each slice is a stroked circle segment (dasharray = its arc, dashoffset = where it starts) —
  // handles the single-slice 100% case that a path-arc approach can't express.
  let offset = 0;
  const segments = slices.map((s, i) => {
    const length = (s.value / total) * CIRCUMFERENCE;
    const segment = { slice: s, index: i, length, offset };
    offset += length;
    return segment;
  });

  return (
    <div className="flex flex-wrap items-center gap-x-8 gap-y-4">
      <svg
        viewBox={`0 0 ${SIZE} ${SIZE}`}
        className="h-44 w-44 shrink-0"
        role="img"
        aria-label={spec.yLabel ? `${spec.yLabel} by ${spec.xField}` : "Breakdown"}
      >
        <g transform={`rotate(-90 ${CENTER} ${CENTER})`}>
          {segments.map((seg) => (
            <circle
              key={seg.slice.label}
              data-testid={`donut-slice-${seg.index}`}
              cx={CENTER}
              cy={CENTER}
              r={RADIUS}
              fill="none"
              strokeWidth={RING}
              strokeDasharray={`${seg.length} ${CIRCUMFERENCE - seg.length}`}
              strokeDashoffset={-seg.offset}
              className={seg.slice.isOther ? OTHER_STROKE : SERIES_STROKE[seg.index]}
            />
          ))}
        </g>
        <text
          x={CENTER}
          y={spec.yLabel ? CENTER - 2 : CENTER + 5}
          textAnchor="middle"
          className="fill-slate-900 dark:fill-slate-100 text-[17px] font-semibold"
          data-testid="donut-total"
        >
          {formatY(total)}
        </text>
        {spec.yLabel && (
          <text x={CENTER} y={CENTER + 15} textAnchor="middle" className="fill-slate-500 dark:fill-slate-400 text-[10px]">
            {spec.yLabel}
          </text>
        )}
      </svg>

      {/* Direct labeling: every segment's name, value, and share — never hover-only. */}
      <ul className="space-y-1.5 text-sm text-slate-600 dark:text-slate-300" data-testid="chart-legend">
        {segments.map((seg) => (
          <li key={seg.slice.label} className="flex items-center gap-2">
            <span
              aria-hidden
              className={`h-2.5 w-2.5 shrink-0 rounded-full ${seg.slice.isOther ? OTHER_BG : SERIES_BG[seg.index]}`}
            />
            <span className="min-w-0 truncate">{seg.slice.label || "(unlabeled)"}</span>
            <span className="ml-auto pl-4 font-medium text-slate-900 dark:text-slate-100">{formatY(seg.slice.value)}</span>
            <span className="w-10 text-right text-xs text-slate-400 dark:text-slate-500">
              {Math.round((seg.slice.value / total) * 100)}%
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
