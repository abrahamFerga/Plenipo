import { createContext, useContext, type ReactNode } from "react";

/**
 * Host branding for the domain shell — the product name and logo shown in the top bar. Supplied via
 * `PlenipoApp`/`AppShell`'s `branding` prop so a host presents its own identity, not "Plenipo". (The accent
 * color is themed separately, via the `--plenipo-brand-*` CSS variables.)
 */
export interface PlenipoBranding {
  /** Product name shown in the top bar. Defaults to "Plenipo". */
  name?: string;
  /** Custom logo node (e.g. an <img> or SVG). Defaults to the Plenipo mark. */
  logo?: ReactNode;
}

export const BrandingContext = createContext<PlenipoBranding>({});

/** The active host branding (empty defaults when no `branding` was provided). */
export function useBranding(): PlenipoBranding {
  return useContext(BrandingContext);
}

/**
 * Resolves the effective product name from the two sources the app shell has:
 * the host's runtime answer (`/api/platform/branding`) wins when it names an actual product;
 * a runtime "Plenipo" is the endpoint's default and must not override a build-time brand
 * (a host that truly wants "Plenipo" gets it anyway — it is also the final fallback).
 */
export function resolveBrandName(
  buildTime: string | undefined,
  runtime: string | undefined,
): string | undefined {
  if (runtime && runtime !== "Plenipo") {
    return runtime;
  }
  return buildTime;
}
