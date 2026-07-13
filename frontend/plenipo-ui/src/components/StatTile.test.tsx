// @vitest-environment jsdom
import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { StatTile } from "./StatTile";

describe("StatTile", () => {
  afterEach(cleanup);

  it("formats numeric values through the shared chart vocabulary", () => {
    render(<StatTile label="Net worth" value={12400} />);

    expect(screen.getByText("Net worth")).toBeTruthy();
    expect(screen.getByText("12,400")).toBeTruthy();
  });

  it("shows string values verbatim — the caller owns currency formatting", () => {
    render(<StatTile label="Safe to spend" value="$340.50" caption="until Friday" />);

    expect(screen.getByText("$340.50")).toBeTruthy();
    expect(screen.getByText("until Friday")).toBeTruthy();
  });

  it("renders a sparkline for a trend with direction", () => {
    render(<StatTile label="Net worth" value={12400} trend={[10000, 11000, 12400]} />);

    const spark = screen.getByTestId("stat-sparkline");
    // Three points → M plus two Ls.
    expect(spark.querySelector("path")?.getAttribute("d")?.match(/[ML]/g)?.length).toBe(3);
  });

  it("renders no sparkline for fewer than two points — one value has no direction", () => {
    render(<StatTile label="Net worth" value={12400} trend={[12400]} />);

    expect(screen.queryByTestId("stat-sparkline")).toBeNull();
  });
});
