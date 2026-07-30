import { describe, expect, it } from "vitest";
import { buildBarGroups, buildDonutSlices, buildLineSeries, MAX_SERIES, niceTicks } from "./charts";
import type { TabChart } from "./api";

/**
 * The shaping is shared by the web SVG and the native canvas, so it is tested here rather than
 * through either renderer: a `chart` spec must produce the same numbers on a phone as in a
 * browser, and that is a property of these functions alone.
 */

const spec = (over: Partial<TabChart> = {}): TabChart => ({ xField: "on", yField: "value", ...over });

describe("buildLineSeries", () => {
  it("groups rows into one series per seriesField value, oldest first", () => {
    const series = buildLineSeries(
      [
        { on: "2026-03-02", value: 2, ccy: "USD" },
        { on: "2026-03-01", value: 1, ccy: "USD" },
        { on: "2026-03-01", value: 9, ccy: "EUR" },
      ],
      spec({ seriesField: "ccy" }),
    );

    expect(series.map((s) => s.name)).toEqual(["EUR", "USD"]);
    // Rows arrive in whatever order the endpoint's query produced; a line has to be chronological.
    expect(series[1].points.map((p) => p.y)).toEqual([1, 2]);
  });

  it("makes one anonymous series when the spec names no series field", () => {
    const series = buildLineSeries([{ on: "2026-03-01", value: 5 }], spec());

    expect(series).toHaveLength(1);
    expect(series[0].name).toBe("");
  });

  it("drops rows it cannot plot instead of charting NaN", () => {
    const series = buildLineSeries(
      [
        { on: "2026-03-01", value: 5 },
        { on: "not a date", value: 5 },
        { on: "2026-03-02", value: "not a number" },
      ],
      spec(),
    );

    expect(series[0].points).toHaveLength(1);
  });

  it("plots a null measure as zero — a sharp edge, pinned so it can't change unnoticed", () => {
    // `Number(null)` is 0, and 0 is finite, so a null y survives the filter and draws a point on
    // the baseline. That asserts "the value was zero" when the data said "unknown", which is the
    // same category of lie as a truncated axis. Kept because it is the web shell's long-standing
    // behaviour and changing it would move existing charts; an endpoint that means "no data"
    // should omit the row rather than send null.
    const series = buildLineSeries([{ on: "2026-03-01", value: null }], spec());

    expect(series[0].points).toEqual([{ x: new Date("2026-03-01").getTime(), y: 0, xLabel: "2026-03-01" }]);
  });

  it("returns every series, including past the cap, so the caller can disclose the overflow", () => {
    const rows = ["a", "b", "c", "d", "e", "f"].map((name) => ({ on: "2026-03-01", value: 1, name }));

    const series = buildLineSeries(rows, spec({ seriesField: "name" }));

    // Truncating here would throw away the count a "+2 more not shown" note needs.
    expect(series.length).toBe(6);
    expect(series.length).toBeGreaterThan(MAX_SERIES);
  });
});

describe("buildDonutSlices", () => {
  it("sums rows sharing a label and orders by size", () => {
    const slices = buildDonutSlices(
      [
        { on: "Rent", value: 100 },
        { on: "Food", value: 300 },
        { on: "Rent", value: 50 },
      ],
      spec(),
    );

    expect(slices).toEqual([
      { label: "Food", value: 300, isOther: false },
      { label: "Rent", value: 150, isOther: false },
    ]);
  });

  it("rolls everything past the cap into a single Other", () => {
    const rows = [10, 9, 8, 7, 6, 5].map((value, i) => ({ on: `cat-${i}`, value }));

    const slices = buildDonutSlices(rows, spec());

    expect(slices).toHaveLength(MAX_SERIES + 1);
    const other = slices[slices.length - 1];
    expect(other.isOther).toBe(true);
    expect(other.value).toBe(11); // 6 + 5
  });

  it("drops values that cannot occupy arc length", () => {
    const slices = buildDonutSlices(
      [
        { on: "Refund", value: -20 },
        { on: "Zero", value: 0 },
        { on: "Real", value: 5 },
      ],
      spec(),
    );

    // A negative share is not a small slice; it is not a slice.
    expect(slices).toEqual([{ label: "Real", value: 5, isOther: false }]);
  });
});

describe("buildBarGroups", () => {
  it("keeps categories in row order, because the endpoint chose that order", () => {
    const bars = buildBarGroups(
      [
        { on: "March", value: 3 },
        { on: "January", value: 1 },
        { on: "February", value: 2 },
      ],
      spec(),
    );

    // Sorting alphabetically here would scramble a month-by-month comparison.
    expect(bars.categories).toEqual(["March", "January", "February"]);
  });

  it("puts null where no row covered a (series, category) pair", () => {
    const bars = buildBarGroups(
      [
        { on: "Jan", value: 5, kind: "income" },
        { on: "Feb", value: 2, kind: "expense" },
      ],
      spec({ seriesField: "kind" }),
    );

    expect(bars.series).toEqual(["income", "expense"]);
    // A missing bar is absent, not zero — zero would assert something the data didn't say.
    expect(bars.values).toEqual([
      [5, null],
      [null, 2],
    ]);
  });

  it("does not let a name containing the key separator merge two different bars", () => {
    // Both halves are arbitrary data from a module's endpoint. With a space separator these two
    // rows produce the identical key "north east sales" and silently sum into one bar. The
    // original web implementation used a NUL for exactly this reason; extracting it here is where
    // that detail is easiest to lose, so it is pinned.
    const bars = buildBarGroups(
      [
        { on: "east sales", value: 1, kind: "north" },
        { on: "sales", value: 2, kind: "north east" },
      ],
      spec({ seriesField: "kind" }),
    );

    expect(bars.categories).toEqual(["east sales", "sales"]);
    expect(bars.series).toEqual(["north", "north east"]);
    expect(bars.values).toEqual([
      [1, null],
      [null, 2],
    ]);
  });

  it("sums rows that collide on the same pair", () => {
    const bars = buildBarGroups(
      [
        { on: "Jan", value: 5 },
        { on: "Jan", value: 3 },
      ],
      spec(),
    );

    expect(bars.values[0][0]).toBe(8);
  });

  it("caps the series and reports how many it dropped", () => {
    const rows = ["a", "b", "c", "d", "e"].map((kind) => ({ on: "Jan", value: 1, kind }));

    const bars = buildBarGroups(rows, spec({ seriesField: "kind" }));

    expect(bars.series).toHaveLength(MAX_SERIES);
    expect(bars.droppedSeries).toBe(1);
  });
});

describe("niceTicks", () => {
  it("picks round values off the 1/2/2.5/5/10 ladder", () => {
    expect(niceTicks(0, 100)).toEqual([0, 25, 50, 75, 100]);
  });

  it("returns the single value when there is no range to divide", () => {
    expect(niceTicks(7, 7)).toEqual([7]);
  });

  it("handles a domain crossing zero", () => {
    expect(niceTicks(-100, 100)).toContain(0);
  });
});
