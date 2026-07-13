import plenipoPreset from "./tailwind-preset.js";

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
