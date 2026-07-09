import { createContext, useContext, type ReactNode } from "react";

/**
 * Host branding for the domain shell — the product name and logo shown in the top bar. Supplied via
 * `CortexApp`/`AppShell`'s `branding` prop so a host presents its own identity, not "Cortex". (The accent
 * color is themed separately, via the `--cortex-brand-*` CSS variables.)
 */
export interface CortexBranding {
  /** Product name shown in the top bar. Defaults to "Cortex". */
  name?: string;
  /** Custom logo node (e.g. an <img> or SVG). Defaults to the Cortex mark. */
  logo?: ReactNode;
}

export const BrandingContext = createContext<CortexBranding>({});

/** The active host branding (empty defaults when no `branding` was provided). */
export function useBranding(): CortexBranding {
  return useContext(BrandingContext);
}

/**
 * Resolves the effective product name from the two sources the app shell has:
 * the host's runtime answer (`/api/platform/branding`) wins when it names an actual product;
 * a runtime "Cortex" is the endpoint's default and must not override a build-time brand
 * (a host that truly wants "Cortex" gets it anyway — it is also the final fallback).
 */
export function resolveBrandName(
  buildTime: string | undefined,
  runtime: string | undefined,
): string | undefined {
  if (runtime && runtime !== "Cortex") {
    return runtime;
  }
  return buildTime;
}
