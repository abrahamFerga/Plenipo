import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { apiGet, type TabChart as TabChartSpec } from "../lib/api";
import { formatY, MAX_SERIES, niceTicks, SERIES_BG, SERIES_FILL, SERIES_STROKE } from "../lib/chartTheme";
import { TabBarChart } from "./TabBarChart";
import { TabDonutChart } from "./TabDonutChart";

/**
 * The shell's server-driven chart — a tab declaring `chart` renders its dataEndpoint rows here
 * instead of the table, with `chart.kind` picking the geometry: the time-series line below
 * (the default), {@link TabDonutChart}, or {@link TabBarChart}. One fetch, shared loading/error
 * handling; each kind owns its empty state (what "no data" means differs per geometry).
 *
 * The line chart is deliberately small and dependency-free: one y-scale (never a dual axis),
 * thin 2px lines, recessive grid, a crosshair+tooltip hover layer, direct labels at line ends
 * (a colored mark carries identity; the text wears text tokens), and a legend once there are
 * two or more series.
 *
 * Series colors are the validated categorical palette shared by every kind (see
 * `lib/chartTheme.ts`). Assigned in fixed order, never cycled: a 5th series folds into the
 * "more" note rather than inventing a hue.
 */

const WIDTH = 720;
const HEIGHT = 280;
const PAD = { top: 16, right: 12, bottom: 28, left: 64 };

interface Point {
  x: number; // epoch ms
  y: number;
  xLabel: string;
}

interface Series {
  name: string;
  points: Point[];
}

function buildSeries(rows: Record<string, unknown>[], spec: TabChartSpec): Series[] {
  const groups = new Map<string, Point[]>();
  for (const row of rows) {
    const rawX = row[spec.xField];
    const rawY = row[spec.yField];
    const x = typeof rawX === "string" || typeof rawX === "number" ? new Date(rawX).getTime() : NaN;
    const y = typeof rawY === "number" ? rawY : Number(rawY);
    if (!Number.isFinite(x) || !Number.isFinite(y)) continue;
    const name = spec.seriesField ? String(row[spec.seriesField] ?? "") : "";
    const list = groups.get(name) ?? [];
    list.push({ x, y, xLabel: String(rawX) });
    groups.set(name, list);
  }
  return [...groups.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([name, points]) => ({ name, points: points.sort((a, b) => a.x - b.x) }));
}

const formatDate = (ms: number) =>
  new Date(ms).toLocaleDateString(undefined, { month: "short", day: "numeric" });

export function TabChartView({ endpoint, spec }: { endpoint: string; spec: TabChartSpec }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<Record<string, unknown>[]>(endpoint),
  });

  if (isLoading) return <p className="text-sm text-slate-500">Loading…</p>;
  if (isError) return <p className="text-sm text-red-600">{(error as Error).message}</p>;

  const rows = data ?? [];
  switch (spec.kind ?? "line") {
    case "donut":
      return <TabDonutChart rows={rows} spec={spec} />;
    case "bar":
      return <TabBarChart rows={rows} spec={spec} />;
    default:
      return <TabLineChart rows={rows} spec={spec} />;
  }
}

function TabLineChart({ rows, spec }: { rows: Record<string, unknown>[]; spec: TabChartSpec }) {
  const [hoverX, setHoverX] = useState<number | null>(null);

  const allSeries = useMemo(() => buildSeries(rows, spec), [rows, spec]);
  const series = allSeries.slice(0, MAX_SERIES);

  if (series.length === 0 || series.every((s) => s.points.length === 0)) {
    return (
      <div className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
        No data points yet — the trend appears once history accumulates.
      </div>
    );
  }

  const xs = series.flatMap((s) => s.points.map((p) => p.x));
  const ys = series.flatMap((s) => s.points.map((p) => p.y));
  const xMin = Math.min(...xs);
  const xMax = Math.max(...xs);
  const ySpread = Math.max(...ys) - Math.min(...ys);
  const yPad = ySpread === 0 ? Math.max(1, Math.abs(ys[0]) * 0.1) : ySpread * 0.08;
  const yMin = Math.min(...ys) - yPad;
  const yMax = Math.max(...ys) + yPad;

  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const sx = (x: number) => PAD.left + (xMax === xMin ? plotW / 2 : ((x - xMin) / (xMax - xMin)) * plotW);
  const sy = (y: number) => PAD.top + plotH - ((y - yMin) / (yMax - yMin)) * plotH;

  const yTicks = niceTicks(yMin, yMax);
  const xTickCount = Math.min(5, new Set(xs).size);
  const xTicks = [...Array(xTickCount)].map((_, i) => xMin + ((xMax - xMin) * i) / Math.max(1, xTickCount - 1));

  // Hover: nearest distinct x across all series (crosshair + one tooltip).
  const distinctXs = [...new Set(xs)].sort((a, b) => a - b);
  const hovered = hoverX === null ? null : distinctXs.reduce((a, b) => (Math.abs(b - hoverX) < Math.abs(a - hoverX) ? b : a));
  const hoveredValues =
    hovered === null
      ? []
      : series
          .map((s, i) => ({ name: s.name, index: i, point: s.points.find((p) => p.x === hovered) }))
          .filter((v) => v.point !== undefined);

  return (
    <div className="space-y-2">
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        className="w-full max-w-3xl"
        role="img"
        aria-label={spec.yLabel ?? "Trend over time"}
        onMouseMove={(e) => {
          const rect = e.currentTarget.getBoundingClientRect();
          const px = ((e.clientX - rect.left) / rect.width) * WIDTH;
          setHoverX(xMin + ((px - PAD.left) / plotW) * (xMax - xMin));
        }}
        onMouseLeave={() => setHoverX(null)}
      >
        {/* Recessive grid + y labels (text tokens, never series color). */}
        {yTicks.map((t) => (
          <g key={t}>
            <line x1={PAD.left} x2={WIDTH - PAD.right} y1={sy(t)} y2={sy(t)} className="stroke-slate-200 dark:stroke-slate-700" strokeWidth="1" />
            <text x={PAD.left - 8} y={sy(t) + 4} textAnchor="end" className="fill-slate-500 dark:fill-slate-400 text-[11px]">
              {formatY(t)}
            </text>
          </g>
        ))}
        {xTicks.map((t) => (
          <text key={t} x={sx(t)} y={HEIGHT - 8} textAnchor="middle" className="fill-slate-500 dark:fill-slate-400 text-[11px]">
            {formatDate(t)}
          </text>
        ))}

        {/* 2px lines; hover markers ≥8px. */}
        {series.map((s, i) => (
          <g key={s.name}>
            <path
              data-testid={`chart-line-${i}`}
              d={s.points.map((p, j) => `${j === 0 ? "M" : "L"}${sx(p.x).toFixed(1)},${sy(p.y).toFixed(1)}`).join(" ")}
              fill="none"
              strokeWidth="2"
              strokeLinejoin="round"
              className={SERIES_STROKE[i]}
            />
          </g>
        ))}

        {hovered !== null && (
          <g>
            <line x1={sx(hovered)} x2={sx(hovered)} y1={PAD.top} y2={HEIGHT - PAD.bottom} className="stroke-slate-300 dark:stroke-slate-600" strokeWidth="1" />
            {hoveredValues.map((v) => (
              <circle key={v.name} cx={sx(hovered)} cy={sy(v.point!.y)} r="4.5" className={`${SERIES_FILL[v.index]} stroke-white dark:stroke-slate-900`} strokeWidth="2" />
            ))}
          </g>
        )}
      </svg>

      {/* Tooltip row (HTML, not SVG — wraps and themes for free). */}
      <div className="min-h-6 text-sm text-slate-600 dark:text-slate-300" data-testid="chart-tooltip">
        {hovered !== null && hoveredValues.length > 0 && (
          <span className="inline-flex flex-wrap items-center gap-x-4 gap-y-1">
            <span className="font-medium">{formatDate(hovered)}</span>
            {hoveredValues.map((v) => (
              <span key={v.name} className="inline-flex items-center gap-1.5">
                <span aria-hidden className={`h-2.5 w-2.5 rounded-full ${SERIES_BG[v.index]}`} />
                {v.name && <span>{v.name}:</span>}
                <span className="font-medium text-slate-900 dark:text-slate-100">{formatY(v.point!.y)}</span>
              </span>
            ))}
          </span>
        )}
      </div>

      {/* Legend once identity needs naming (≥2 series); a single series is named by the tab. */}
      {series.length >= 2 && (
        <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm text-slate-600 dark:text-slate-300" data-testid="chart-legend">
          {series.map((s, i) => (
            <span key={s.name} className="inline-flex items-center gap-1.5">
              <span aria-hidden className={`h-2.5 w-2.5 rounded-full ${SERIES_BG[i]}`} />
              {s.name || `Series ${i + 1}`}
            </span>
          ))}
          {allSeries.length > MAX_SERIES && (
            <span className="text-slate-400">+{allSeries.length - MAX_SERIES} more not shown</span>
          )}
        </div>
      )}
    </div>
  );
}
