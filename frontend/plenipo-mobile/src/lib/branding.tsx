import { createContext, useContext, type ReactNode } from "react";

/**
 * A product's identity in the shell: the name in the header and an optional logo node. Colors are
 * not here — they are theme tokens (see theme.ts), the same split the web shell makes between
 * content and CSS variables.
 */
export interface PlenipoBranding {
  /** Product name shown in the header. Falls back to what `GET /api/platform/branding` reports. */
  name?: string;
  /** Optional logo element, rendered in place of the name when supplied. */
  logo?: ReactNode;
}

export const BrandingContext = createContext<PlenipoBranding>({});

export function useBranding(): PlenipoBranding {
  return useContext(BrandingContext);
}
