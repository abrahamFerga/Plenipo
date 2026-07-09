import { describe, expect, it } from "vitest";
import { resolveBrandName } from "./branding";

// One prebuilt bundle, every product: the runtime answer wins when it names a product,
// the build-time bake is the fallback, and the platform default never masks either.
describe("resolveBrandName", () => {
  it("prefers the host's runtime product name", () => {
    expect(resolveBrandName("Casewell", "Networthy")).toBe("Networthy");
    expect(resolveBrandName(undefined, "Networthy")).toBe("Networthy");
  });

  it("does not let the runtime default override a build-time brand", () => {
    expect(resolveBrandName("Casewell", "Cortex")).toBe("Casewell");
  });

  it("falls back to the build-time brand when the endpoint is silent", () => {
    expect(resolveBrandName("Casewell", undefined)).toBe("Casewell");
  });

  it("resolves to nothing when neither source brands (AppShell defaults to Cortex)", () => {
    expect(resolveBrandName(undefined, "Cortex")).toBeUndefined();
    expect(resolveBrandName(undefined, undefined)).toBeUndefined();
  });
});
