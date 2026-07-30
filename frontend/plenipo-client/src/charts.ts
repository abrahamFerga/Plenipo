import type { TabChart } from "./api";

/**
 * Turning a tab's `dataEndpoint` rows into plottable shapes.
 *
 * This is the part of charting that has nothing to do with pixels: which rows become which
 * series, how ties are summed, where the tail rolls into "Other", what a readable tick is. It
 * lives here so the web shell's SVG and the mobile shell's native canvas plot *the same numbers*
 * from the same manifest — a `chart` spec must not mean two different things depending on which
 * screen you read it on.
 *
 * Geometry, color, and interaction stay with each renderer, where they belong.
 */

/**
 * How many named series or segments a chart shows before the rest roll up. Fixed, not cycled: a
 * fifth series folds into "Other"/"+N more" rather than reusing a hue and implying a relationship
 * that isn't there. Each renderer's palette must offer exactly this many distinct colors.
 */
export const MAX_SERIES = 4;

/** A number formatted for an axis or a legend — grouped, and never with noise digits past 1000. */
export const formatY = (v: number): string =>
  Math.abs(v) >= 1000 ? v.toLocaleString(undefined, { maximumFractionDigits: 0 }) : v.toLocaleString();

/** Round tick values covering [min, max] — the 1/2/2.5/5/10 ladder people actually read. */
export function niceTicks(min: number, max: number, count = 4): number[] {
  if (min === max) {
    return [min];
  }
  const step = (max - min) / count;
  const magnitude = 10 ** Math.floor(Math.log10(step));
  const nice = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= step) ?? step;
  const start = Math.ceil(min / nice) * nice;
  const ticks: number[] = [];
  for (let v = start; v <= max + 1e-9; v += nice) ticks.push(v);
  return ticks;
}

/** One plotted point of a line series. `x` is epoch ms; `xLabel` is the row's original value. */
export interface ChartPoint {
  x: number;
  y: number;
  xLabel: string;
}

/** One line of a time series, points sorted oldest-first. */
export interface ChartSeries {
  name: string;
  points: ChartPoint[];
}

const toNumber = (raw: unknown): number => (typeof raw === "number" ? raw : Number(raw));

/**
 * Line geometry: rows grouped into one series per distinct `seriesField` value (one anonymous
 * series when the spec declares none), each sorted by time. Rows whose x isn't a date or whose y
 * isn't a number are dropped — a chart that silently plots NaN is worse than one that plots less.
 *
 * Returns EVERY series, including any past {@link MAX_SERIES}: the caller decides how to disclose
 * the overflow ("+2 more not shown"), which it can't do if the count is thrown away here.
 */
export function buildLineSeries(rows: Record<string, unknown>[], spec: TabChart): ChartSeries[] {
  const groups = new Map<string, ChartPoint[]>();
  for (const row of rows) {
    const rawX = row[spec.xField];
    const x = typeof rawX === "string" || typeof rawX === "number" ? new Date(rawX).getTime() : NaN;
    const y = toNumber(row[spec.yField]);
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

/** One wedge of a donut. `isOther` marks the rolled-up tail, which renders recessively. */
export interface ChartSlice {
  label: string;
  value: number;
  isOther: boolean;
}

/**
 * Donut geometry: rows summed per `xField` label, largest first, with everything past
 * {@link MAX_SERIES} rolled into a single "Other".
 *
 * Non-positive and non-finite values are dropped rather than clamped — a share has to occupy arc
 * length, and a negative one cannot. `seriesField` is ignored, as the descriptor documents.
 */
export function buildDonutSlices(rows: Record<string, unknown>[], spec: TabChart): ChartSlice[] {
  const totals = new Map<string, number>();
  for (const row of rows) {
    const value = toNumber(row[spec.yField]);
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

/** A grouped-bar chart's categories, series, and the value at each intersection. */
export interface ChartBars {
  /** Category labels in ROW order — the endpoint decides the ordering, not this function. */
  categories: string[];
  /** Series names, capped at {@link MAX_SERIES}. */
  series: string[];
  /** values[seriesIndex][categoryIndex]; null where no row covered that pair. */
  values: (number | null)[][];
  /** How many series were dropped by the cap, so the renderer can say so. */
  droppedSeries: number;
}

/**
 * Joins a (series, category) pair into a lookup key.
 *
 * The separator is U+0000 rather than something readable because both halves are arbitrary data
 * from a module's endpoint. A space would make series "north east" + category "sales" collide
 * with series "north" + category "east sales", silently summing two unrelated bars into one. NUL
 * cannot appear in either half. Written as an escape, not a literal control character, so it
 * survives being read, copied, and pasted.
 */
const compositeKey = (series: string, category: string) => `${series}\u0000${category}`;

/**
 * Bar geometry: one bar per (series, category) pair, values summed where rows collide. Category
 * order follows first appearance in the rows, deliberately — a month-by-month comparison is
 * ordered by the endpoint's query, and re-sorting alphabetically here would scramble it.
 */
export function buildBarGroups(rows: Record<string, unknown>[], spec: TabChart): ChartBars {
  const categories: string[] = [];
  const series: string[] = [];
  const totals = new Map<string, number>();

  for (const row of rows) {
    const y = toNumber(row[spec.yField]);
    if (!Number.isFinite(y)) continue;
    const category = String(row[spec.xField] ?? "");
    const s = spec.seriesField ? String(row[spec.seriesField] ?? "") : "";
    if (!categories.includes(category)) categories.push(category);
    if (!series.includes(s)) series.push(s);
    const key = compositeKey(s, category);
    totals.set(key, (totals.get(key) ?? 0) + y);
  }

  const kept = series.slice(0, MAX_SERIES);
  return {
    categories,
    series: kept,
    values: kept.map((s) => categories.map((c) => totals.get(compositeKey(s, c)) ?? null)),
    droppedSeries: series.length - kept.length,
  };
}
