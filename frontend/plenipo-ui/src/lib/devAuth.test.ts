import { describe, expect, it } from "vitest";
import { normalizeApiBase } from "./devAuth";

describe("normalizeApiBase", () => {
  it("defaults to localhost:8080 when VITE_API_BASE is unset", () => {
    expect(normalizeApiBase(undefined)).toBe("http://localhost:8080");
  });

  it("strips trailing slash(es) so request paths don't double up", () => {
    expect(normalizeApiBase("http://api.example.com/")).toBe("http://api.example.com");
    expect(normalizeApiBase("http://api.example.com//")).toBe("http://api.example.com");
  });

  it("leaves an already-clean base unchanged", () => {
    expect(normalizeApiBase("http://api.example.com")).toBe("http://api.example.com");
    expect(normalizeApiBase("http://localhost:8080")).toBe("http://localhost:8080");
  });
});
