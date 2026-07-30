import { afterEach, describe, expect, it, vi } from "vitest";
import { resolveFieldDefault, resolveFieldDefaults } from "./fieldDefaults";
import type { TabEditorField } from "./api";

const field = (over: Partial<TabEditorField> = {}): TabEditorField => ({
  field: "timeZoneId",
  label: "Time zone",
  ...over,
});

/** Pin the "browser" to a known zone; jsdom otherwise reports whatever CI is set to. */
function browserSaysTimeZone(zone: string) {
  vi.spyOn(Intl, "DateTimeFormat").mockReturnValue({
    resolvedOptions: () => ({ timeZone: zone }),
  } as unknown as Intl.DateTimeFormat);
}

/** Pin the browser's UI language, which is where the currency guess derives its region. */
function browserSaysLanguage(lang: string) {
  vi.spyOn(globalThis.navigator, "language", "get").mockReturnValue(lang);
}

afterEach(() => vi.restoreAllMocks());

describe("resolveFieldDefault", () => {
  it("is empty when the field declares nothing — the shell still never guesses", () => {
    expect(resolveFieldDefault(field())).toBe("");
    expect(resolveFieldDefault(field({ options: [{ value: "UTC", label: "UTC" }] }))).toBe("");
  });

  it("uses a constant the manifest declared", () => {
    // A text field has no vocabulary to contradict the default.
    expect(resolveFieldDefault(field({ default: "UTC" }))).toBe("UTC");
    expect(
      resolveFieldDefault(field({ default: "UTC", options: [{ value: "UTC", label: "UTC" }] })),
    ).toBe("UTC");
  });

  it("refuses a constant the field's own vocabulary does not contain", () => {
    expect(
      resolveFieldDefault(field({ default: "Etc/Nowhere", options: [{ value: "UTC", label: "UTC" }] })),
    ).toBe("");
  });

  it("fills a time zone from the browser, so nobody hunts for where they live", () => {
    browserSaysTimeZone("America/Mexico_City");
    const f = field({
      defaultFrom: "browser-timezone",
      options: [
        { value: "UTC", label: "UTC" },
        { value: "America/Mexico_City", label: "Mexico City" },
      ],
    });
    expect(resolveFieldDefault(f)).toBe("America/Mexico_City");
  });

  it("refuses a browser zone the field does not offer, rather than posting a value it would reject", () => {
    browserSaysTimeZone("Mars/Olympus_Mons");
    const f = field({
      defaultFrom: "browser-timezone",
      options: [{ value: "UTC", label: "UTC" }],
    });
    expect(resolveFieldDefault(f)).toBe("");
  });

  it("prefers what the browser knows over a declared constant", () => {
    browserSaysTimeZone("Europe/Madrid");
    const f = field({
      default: "UTC",
      defaultFrom: "browser-timezone",
      options: [
        { value: "UTC", label: "UTC" },
        { value: "Europe/Madrid", label: "Madrid" },
      ],
    });
    expect(resolveFieldDefault(f)).toBe("Europe/Madrid");
  });

  it("falls back to the constant when the browser cannot say", () => {
    vi.spyOn(Intl, "DateTimeFormat").mockImplementation(() => {
      throw new Error("no Intl here");
    });
    const f = field({
      default: "UTC",
      defaultFrom: "browser-timezone",
      options: [{ value: "UTC", label: "UTC" }],
    });
    expect(resolveFieldDefault(f)).toBe("UTC");
  });

  it("ignores an unknown source instead of throwing", () => {
    expect(resolveFieldDefault(field({ defaultFrom: "browser-astrology" }))).toBe("");
  });

  it("leaves options loaded from an endpoint alone — they are unknown at seed time", () => {
    const f = field({ default: "Chase Checking", optionsEndpoint: "/api/finance/accounts" });
    expect(resolveFieldDefault(f)).toBe("Chase Checking");
  });

  const currencyField = (over: Partial<TabEditorField> = {}) =>
    field({
      field: "currencyCode",
      label: "Currency",
      defaultFrom: "browser-currency",
      options: [
        { value: "USD", label: "USD" },
        { value: "MXN", label: "MXN" },
        { value: "EUR", label: "EUR" },
      ],
      ...over,
    });

  it("guesses the currency from a region-tagged browser language", () => {
    browserSaysLanguage("es-MX");
    expect(resolveFieldDefault(currencyField())).toBe("MXN");
  });

  it("fills in the likely region when the language omits it (en → US → USD)", () => {
    browserSaysLanguage("en");
    expect(resolveFieldDefault(currencyField())).toBe("USD");
  });

  it("maps a eurozone locale to EUR", () => {
    browserSaysLanguage("de-DE");
    expect(resolveFieldDefault(currencyField())).toBe("EUR");
  });

  it("offers no currency guess for a region the map doesn't cover, rather than a wrong one", () => {
    browserSaysLanguage("es-BO"); // Bolivia — a real region, deliberately absent from the guess map
    expect(resolveFieldDefault(currencyField())).toBe("");
  });

  it("still refuses a guessed currency the field does not offer", () => {
    browserSaysLanguage("ja-JP"); // JPY, absent from these options
    expect(resolveFieldDefault(currencyField())).toBe("");
  });
});

describe("resolveFieldDefaults", () => {
  it("keys every field, defaulted or not", () => {
    browserSaysTimeZone("America/Mexico_City");
    const values = resolveFieldDefaults([
      field({ field: "name", label: "Name" }),
      field({
        field: "timeZoneId",
        defaultFrom: "browser-timezone",
        options: [{ value: "America/Mexico_City", label: "Mexico City" }],
      }),
    ]);
    expect(values).toEqual({ name: "", timeZoneId: "America/Mexico_City" });
  });
});
