import { useMemo } from "react";
import { StyleSheet, Text, View, useWindowDimensions } from "react-native";
import Svg, { Circle, G, Line, Path, Rect, Text as SvgText } from "react-native-svg";
import { useQuery } from "@tanstack/react-query";
import {
  apiGet,
  buildBarGroups,
  buildDonutSlices,
  buildLineSeries,
  formatY,
  MAX_SERIES,
  niceTicks,
  type TabChart as TabChartSpec,
} from "@plenipo/client";
import { space, type, useResolvedTheme, type PlenipoTheme } from "../theme";
import { EmptyState, ErrorNote, Loading } from "./ui";

/**
 * The server-driven chart, drawn with react-native-svg.
 *
 * The numbers come from @plenipo/client's shaping — the same functions the web shell uses — so a
 * `chart` spec cannot mean one thing in a browser and another on a phone. Only the drawing is
 * local, and it is deliberately plainer than the web's: no hover (there is no pointer), so
 * identity and value are stated in a legend that is always visible rather than revealed on
 * interaction.
 */

const HEIGHT = 220;
const PAD = { top: 12, right: 12, bottom: 26, left: 56 };

export function TabChartView({ endpoint, spec }: { endpoint: string; spec: TabChartSpec }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<Record<string, unknown>[]>(endpoint),
  });

  if (isLoading) return <Loading />;
  if (isError) return <ErrorNote error={error} />;

  const rows = data ?? [];
  switch (spec.kind ?? "line") {
    case "donut":
      return <DonutChart rows={rows} spec={spec} />;
    case "bar":
      return <BarChart rows={rows} spec={spec} />;
    default:
      return <LineChart rows={rows} spec={spec} />;
  }
}

/** A colored dot plus a label — how every kind states series identity, since hover isn't available. */
function LegendRow({ color, label, value }: { color: string; label: string; value?: string }) {
  const t = useResolvedTheme();
  return (
    <View style={styles.legendRow}>
      <View style={[styles.swatch, { backgroundColor: color }]} />
      <Text style={{ ...type.caption, color: t.textMuted, flex: 1 }} numberOfLines={1}>
        {label}
      </Text>
      {value != null && <Text style={{ ...type.caption, color: t.text, fontWeight: "600" }}>{value}</Text>}
    </View>
  );
}

const formatDate = (ms: number) =>
  new Date(ms).toLocaleDateString(undefined, { month: "short", day: "numeric" });

/** The plot width available inside the page's padding. */
function usePlotWidth(): number {
  const { width } = useWindowDimensions();
  return Math.max(280, width - space.lg * 2);
}

function LineChart({ rows, spec }: { rows: Record<string, unknown>[]; spec: TabChartSpec }) {
  const t = useResolvedTheme();
  const width = usePlotWidth();
  const allSeries = useMemo(() => buildLineSeries(rows, spec), [rows, spec]);
  const series = allSeries.slice(0, MAX_SERIES);

  if (series.length === 0 || series.every((s) => s.points.length === 0)) {
    return <EmptyState text="No data points yet — the trend appears once history accumulates." />;
  }

  const xs = series.flatMap((s) => s.points.map((p) => p.x));
  const ys = series.flatMap((s) => s.points.map((p) => p.y));
  const xMin = Math.min(...xs);
  const xMax = Math.max(...xs);
  const spread = Math.max(...ys) - Math.min(...ys);
  const pad = spread === 0 ? Math.max(1, Math.abs(ys[0]) * 0.1) : spread * 0.08;
  const yMin = Math.min(...ys) - pad;
  const yMax = Math.max(...ys) + pad;

  const plotW = width - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const sx = (x: number) => PAD.left + (xMax === xMin ? plotW / 2 : ((x - xMin) / (xMax - xMin)) * plotW);
  const sy = (y: number) => PAD.top + plotH - ((y - yMin) / (yMax - yMin)) * plotH;

  const yTicks = niceTicks(yMin, yMax);
  const xTickCount = Math.min(4, new Set(xs).size);
  const xTicks = [...Array(xTickCount)].map(
    (_, i) => xMin + ((xMax - xMin) * i) / Math.max(1, xTickCount - 1),
  );

  return (
    <View>
      <Svg width={width} height={HEIGHT} accessibilityLabel={spec.yLabel ?? "Trend over time"}>
        {yTicks.map((tick) => (
          <G key={tick}>
            <Line
              x1={PAD.left}
              x2={width - PAD.right}
              y1={sy(tick)}
              y2={sy(tick)}
              stroke={t.border}
              strokeWidth={1}
            />
            <SvgText x={PAD.left - 6} y={sy(tick) + 4} fontSize={10} fill={t.textMuted} textAnchor="end">
              {formatY(tick)}
            </SvgText>
          </G>
        ))}
        {xTicks.map((tick) => (
          <SvgText
            key={tick}
            x={sx(tick)}
            y={HEIGHT - 6}
            fontSize={10}
            fill={t.textMuted}
            textAnchor="middle"
          >
            {formatDate(tick)}
          </SvgText>
        ))}

        {series.map((s, i) => (
          <G key={s.name}>
            <Path
              d={s.points.map((p, j) => `${j === 0 ? "M" : "L"}${sx(p.x).toFixed(1)},${sy(p.y).toFixed(1)}`).join(" ")}
              fill="none"
              stroke={t.series[i]}
              strokeWidth={2}
              strokeLinejoin="round"
            />
            {/* A single point draws no line, so mark it — otherwise the chart looks empty. */}
            {s.points.length === 1 && (
              <Circle cx={sx(s.points[0].x)} cy={sy(s.points[0].y)} r={3.5} fill={t.series[i]} />
            )}
          </G>
        ))}
      </Svg>

      <View style={{ marginTop: space.sm }}>
        {series.map((s, i) => (
          <LegendRow
            key={s.name}
            color={t.series[i]}
            label={s.name || spec.yLabel || `Series ${i + 1}`}
            value={formatY(s.points[s.points.length - 1].y)}
          />
        ))}
        {allSeries.length > MAX_SERIES && (
          <Text style={{ ...type.caption, color: t.textMuted, marginTop: space.xs }}>
            +{allSeries.length - MAX_SERIES} more not shown
          </Text>
        )}
      </View>
    </View>
  );
}

function DonutChart({ rows, spec }: { rows: Record<string, unknown>[]; spec: TabChartSpec }) {
  const t = useResolvedTheme();
  const slices = useMemo(() => buildDonutSlices(rows, spec), [rows, spec]);

  if (slices.length === 0) {
    return <EmptyState text="No data points yet — the breakdown appears once data accumulates." />;
  }

  const size = 168;
  const center = size / 2;
  const r = 60;
  const ring = 26;
  const circumference = 2 * Math.PI * r;
  const total = slices.reduce((sum, s) => sum + s.value, 0);

  // Each slice is a stroked circle segment (dasharray = its arc) — this handles the single-slice
  // 100% case that a path-arc approach cannot express.
  let offset = 0;
  const segments = slices.map((s, i) => {
    const length = (s.value / total) * circumference;
    const segment = { slice: s, color: sliceColor(t, s.isOther, i), length, offset };
    offset += length;
    return segment;
  });

  return (
    <View>
      <View style={{ alignItems: "center" }}>
        <Svg width={size} height={size} accessibilityLabel={spec.yLabel ?? "Breakdown"}>
          <G rotation={-90} origin={`${center}, ${center}`}>
            {segments.map((seg) => (
              <Circle
                key={seg.slice.label}
                cx={center}
                cy={center}
                r={r}
                fill="none"
                stroke={seg.color}
                strokeWidth={ring}
                strokeDasharray={`${seg.length} ${circumference - seg.length}`}
                strokeDashoffset={-seg.offset}
              />
            ))}
          </G>
          {/* The total sits in the hole: the one number a proportional view implies is stated. */}
          <SvgText
            x={center}
            y={center + 5}
            fontSize={15}
            fontWeight="600"
            fill={t.text}
            textAnchor="middle"
          >
            {formatY(total)}
          </SvgText>
        </Svg>
      </View>

      <View style={{ marginTop: space.md }}>
        {segments.map((seg) => (
          <LegendRow
            key={seg.slice.label}
            color={seg.color}
            label={seg.slice.label || "—"}
            value={`${formatY(seg.slice.value)}  ·  ${Math.round((seg.slice.value / total) * 100)}%`}
          />
        ))}
      </View>
    </View>
  );
}

const sliceColor = (t: PlenipoTheme, isOther: boolean, index: number) =>
  isOther ? t.seriesOther : t.series[index % t.series.length];

function BarChart({ rows, spec }: { rows: Record<string, unknown>[]; spec: TabChartSpec }) {
  const t = useResolvedTheme();
  const width = usePlotWidth();
  const data = useMemo(() => buildBarGroups(rows, spec), [rows, spec]);

  if (data.categories.length === 0) {
    return <EmptyState text="No data points yet — the comparison appears once data accumulates." />;
  }

  const flat = data.values.flat().filter((v): v is number => v !== null);
  // Bars encode by length from zero, so the domain must include it — a truncated baseline lies.
  const yMin = Math.min(0, ...flat);
  const yMax = Math.max(0, ...flat);
  const range = yMax - yMin || 1;

  const plotW = width - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const sy = (y: number) => PAD.top + plotH - ((y - yMin) / range) * plotH;
  const zeroY = sy(0);

  const groupW = plotW / data.categories.length;
  const barW = Math.max(4, (groupW * 0.7) / Math.max(1, data.series.length));
  const yTicks = niceTicks(yMin, yMax);

  return (
    <View>
      <Svg width={width} height={HEIGHT} accessibilityLabel={spec.yLabel ?? "Comparison by category"}>
        {yTicks.map((tick) => (
          <G key={tick}>
            <Line
              x1={PAD.left}
              x2={width - PAD.right}
              y1={sy(tick)}
              y2={sy(tick)}
              stroke={t.border}
              strokeWidth={1}
            />
            <SvgText x={PAD.left - 6} y={sy(tick) + 4} fontSize={10} fill={t.textMuted} textAnchor="end">
              {formatY(tick)}
            </SvgText>
          </G>
        ))}

        {data.categories.map((category, ci) => {
          const groupLeft = PAD.left + ci * groupW + (groupW - barW * data.series.length) / 2;
          return (
            <G key={category}>
              {data.series.map((_, si) => {
                const value = data.values[si][ci];
                if (value === null) return null;
                const top = Math.min(sy(value), zeroY);
                const height = Math.max(1, Math.abs(sy(value) - zeroY));
                return (
                  <Rect
                    key={si}
                    x={groupLeft + si * barW}
                    y={top}
                    width={Math.max(2, barW - 1)}
                    height={height}
                    fill={t.series[si]}
                    rx={2}
                  />
                );
              })}
              <SvgText
                x={PAD.left + ci * groupW + groupW / 2}
                y={HEIGHT - 6}
                fontSize={10}
                fill={t.textMuted}
                textAnchor="middle"
              >
                {truncate(category)}
              </SvgText>
            </G>
          );
        })}

        {/* Zero is emphasized: negative bars hang below it and the reader must see where it is. */}
        <Line
          x1={PAD.left}
          x2={width - PAD.right}
          y1={zeroY}
          y2={zeroY}
          stroke={t.textMuted}
          strokeWidth={1.5}
        />
      </Svg>

      {data.series.length >= 2 && (
        <View style={{ marginTop: space.sm }}>
          {data.series.map((name, i) => (
            <LegendRow key={name} color={t.series[i]} label={name || `Series ${i + 1}`} />
          ))}
        </View>
      )}
      {data.droppedSeries > 0 && (
        <Text style={{ ...type.caption, color: t.textMuted, marginTop: space.xs }}>
          +{data.droppedSeries} more not shown
        </Text>
      )}
    </View>
  );
}

/** Category labels have very little room on a phone; the legend and detail carry the full text. */
const truncate = (label: string) => (label.length > 8 ? `${label.slice(0, 7)}…` : label);

const styles = StyleSheet.create({
  legendRow: { flexDirection: "row", alignItems: "center", gap: space.sm, paddingVertical: 3 },
  swatch: { width: 10, height: 10, borderRadius: 5 },
});
