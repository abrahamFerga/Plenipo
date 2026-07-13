/**
 * Plenipo theme preset — a `brand` color scale backed by CSS variables so a host can rebrand the UI by
 * overriding the `--plenipo-brand-*` custom properties (see the defaults shipped in the package stylesheet)
 * instead of touching component classes. Consumers that run their own Tailwind include this preset:
 *
 *   // tailwind.config.js
 *   import plenipoPreset from "@plenipo/ui/tailwind-preset";
 *   export default { presets: [plenipoPreset], content: [..., "./node_modules/@plenipo/ui/dist/**\/*.js"] };
 *
 * @type {import('tailwindcss').Config}
 */
export default {
  theme: {
    extend: {
      colors: {
        // `<alpha-value>` lets opacity modifiers (bg-brand-600/50) keep working against the CSS var.
        brand: {
          50: "rgb(var(--plenipo-brand-50) / <alpha-value>)",
          100: "rgb(var(--plenipo-brand-100) / <alpha-value>)",
          200: "rgb(var(--plenipo-brand-200) / <alpha-value>)",
          300: "rgb(var(--plenipo-brand-300) / <alpha-value>)",
          400: "rgb(var(--plenipo-brand-400) / <alpha-value>)",
          500: "rgb(var(--plenipo-brand-500) / <alpha-value>)",
          600: "rgb(var(--plenipo-brand-600) / <alpha-value>)",
          700: "rgb(var(--plenipo-brand-700) / <alpha-value>)",
          800: "rgb(var(--plenipo-brand-800) / <alpha-value>)",
          900: "rgb(var(--plenipo-brand-900) / <alpha-value>)",
        },
      },
    },
  },
};
