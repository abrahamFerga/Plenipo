import { useColorScheme } from "react-native";

/**
 * The shell's visual tokens.
 *
 * The web shell rebrands through CSS variables (`--plenipo-brand-600`); React Native has no
 * cascade, so the same idea becomes a token object a product overrides at the root. Same
 * contract, same names where they exist on both sides: set the brand ramp and the whole shell
 * follows — nav indicators, primary buttons, links, chart series, focus states.
 *
 * Light and dark are both first-class. The palette steps are the ones the web shell already
 * validated against its surfaces, so a product that themes one gets a matching other.
 */
export interface PlenipoTheme {
  /** Page background. */
  background: string;
  /** Raised surfaces: cards, sheets, the composer. */
  surface: string;
  /** A surface that needs to read as recessive (a table header, an assistant bubble). */
  surfaceMuted: string;
  border: string;
  text: string;
  textMuted: string;
  /** Primary action color — the brand. */
  brand: string;
  /** Brand text/icons on a plain background, contrast-adjusted per scheme. */
  brandText: string;
  /** Foreground on a brand-filled surface. */
  onBrand: string;
  danger: string;
  success: string;
  /** Categorical series colors — exactly MAX_SERIES of them (see @plenipo/client). */
  series: readonly [string, string, string, string];
  /** The rolled-up "Other" segment: deliberately recessive. */
  seriesOther: string;
}

export const lightTheme: PlenipoTheme = {
  background: "#f8fafc",
  surface: "#ffffff",
  surfaceMuted: "#f1f5f9",
  border: "#e2e8f0",
  text: "#0f172a",
  textMuted: "#64748b",
  brand: "#4f46e5",
  brandText: "#4338ca",
  onBrand: "#ffffff",
  danger: "#dc2626",
  success: "#059669",
  series: ["#2a78d6", "#1baf7a", "#eda100", "#4a3aa7"],
  seriesOther: "#94a3b8",
};

export const darkTheme: PlenipoTheme = {
  background: "#0f172a",
  surface: "#111c33",
  surfaceMuted: "#1e293b",
  border: "#334155",
  text: "#f1f5f9",
  textMuted: "#94a3b8",
  brand: "#6366f1",
  brandText: "#a5b4fc",
  onBrand: "#ffffff",
  danger: "#f87171",
  success: "#34d399",
  series: ["#3987e5", "#199e70", "#c98500", "#9085e9"],
  seriesOther: "#64748b",
};

/**
 * A product's overrides. Partial by design: setting `brand` alone is the common case and must not
 * require restating twenty tokens.
 */
export interface PlenipoThemeOverride {
  light?: Partial<PlenipoTheme>;
  dark?: Partial<PlenipoTheme>;
  /** Applied to both schemes — the shorthand for "our brand is this色". */
  both?: Partial<PlenipoTheme>;
}

export function resolveTheme(scheme: "light" | "dark", override?: PlenipoThemeOverride): PlenipoTheme {
  const base = scheme === "dark" ? darkTheme : lightTheme;
  const perScheme = scheme === "dark" ? override?.dark : override?.light;
  return { ...base, ...override?.both, ...perScheme };
}

/** The theme for the device's current appearance setting, with the product's overrides applied. */
export function useResolvedTheme(override?: PlenipoThemeOverride): PlenipoTheme {
  const scheme = useColorScheme();
  return resolveTheme(scheme === "dark" ? "dark" : "light", override);
}

/** Type-scale and spacing, kept in one place so screens don't invent their own. */
export const type = {
  title: { fontSize: 20, fontWeight: "600" },
  heading: { fontSize: 16, fontWeight: "600" },
  body: { fontSize: 15 },
  label: { fontSize: 13, fontWeight: "500" },
  caption: { fontSize: 12 },
} as const;

export const space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 } as const;

export const radius = { sm: 6, md: 10, lg: 14 } as const;

/**
 * Minimum tappable size. 44pt is Apple's floor and Android's is 48dp; the shell uses 44 as the
 * hard minimum for every interactive element, which is why row actions are buttons with padding
 * rather than the dense text links the web table uses.
 */
export const HIT_SIZE = 44;
