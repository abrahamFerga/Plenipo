// Share the same brand color token as @plenipo/ui so the admin console themes with the domain UI.
import plenipoPreset from "../plenipo-ui/tailwind-preset.js";

/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{js,ts,jsx,tsx}"],
  darkMode: "selector",
  presets: [plenipoPreset],
  theme: {
    extend: {},
  },
  plugins: [],
};
