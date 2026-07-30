import { describe, expect, it } from "vitest";
import { effectiveColumns, maskValue, resolveRowUrl } from "./rows";

describe("resolveRowUrl", () => {
  it("substitutes a placeholder from the row", () => {
    expect(resolveRowUrl("/api/legal/matters/{id}/close", { id: 42 })).toBe("/api/legal/matters/42/close");
  });

  it("substitutes every placeholder, not just the first", () => {
    expect(resolveRowUrl("/api/{module}/{id}", { module: "legal", id: "m1" })).toBe("/api/legal/m1");
  });

  it("url-encodes the value, so a slug with a slash can't rewrite the path", () => {
    expect(resolveRowUrl("/api/legal/clauses/{slug}", { slug: "force majeure/v2" })).toBe(
      "/api/legal/clauses/force%20majeure%2Fv2",
    );
  });

  it("resolves a missing field to empty rather than leaving a literal placeholder", () => {
    // A request to ".../{id}/close" would 404 confusingly; ".../close" fails honestly.
    expect(resolveRowUrl("/api/x/{id}/close", {})).toBe("/api/x//close");
  });
});

describe("effectiveColumns", () => {
  it("uses what the manifest declared", () => {
    const declared = [{ field: "title", header: "Matter" }];
    expect(effectiveColumns(declared, [{ title: "a", other: "b" }])).toBe(declared);
  });

  it("falls back to the row's own fields, minus id", () => {
    expect(effectiveColumns([], [{ id: "1", title: "a", court: "b" }])).toEqual([
      { field: "title", header: "title" },
      { field: "court", header: "court" },
    ]);
  });

  it("yields nothing to render when there are no rows and no declaration", () => {
    expect(effectiveColumns(undefined, [])).toEqual([]);
  });
});

describe("maskValue", () => {
  it("shows the last four — enough to recognise, not enough to read off a screen", () => {
    expect(maskValue("4111111111111234")).toBe("••••1234");
  });

  it("reveals nothing at all when the value is too short to keep a tail secret", () => {
    expect(maskValue("1234")).toBe("••••");
    expect(maskValue("7")).toBe("••••");
  });
});
