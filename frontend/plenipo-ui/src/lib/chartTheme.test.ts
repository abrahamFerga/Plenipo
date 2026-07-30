import { describe, expect, it } from "vitest";
import { MAX_SERIES, OTHER_BG, SERIES_BG, SERIES_FILL, SERIES_STROKE } from "./chartTheme";

/**
 * The shaping caps series at MAX_SERIES (in @plenipo/client, shared with the mobile shell) while
 * the palette that colors them lives here. Two places, one number — so pin it: a fifth hue added
 * without raising the cap would never be used, and raising the cap without a fifth hue would
 * index off the end of the array and render an uncolored line.
 */
describe("the palette matches the shaping cap", () => {
  it.each([
    ["SERIES_STROKE", SERIES_STROKE],
    ["SERIES_FILL", SERIES_FILL],
    ["SERIES_BG", SERIES_BG],
  ])("%s offers exactly MAX_SERIES hues", (_name, palette) => {
    expect(palette).toHaveLength(MAX_SERIES);
  });

  it("keeps the rolled-up tail visually distinct from every named series", () => {
    expect(SERIES_BG).not.toContain(OTHER_BG);
  });
});
