// Share the same brand color token as @abrahamferga/cortex-ui so the admin console themes with the domain UI.
import cortexPreset from "../cortex-ui/tailwind-preset.js";

/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{js,ts,jsx,tsx}"],
  darkMode: "selector",
  presets: [cortexPreset],
  theme: {
    extend: {},
  },
  plugins: [],
};
