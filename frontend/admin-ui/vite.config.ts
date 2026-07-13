import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath } from "node:url";

// The admin console is a standalone SPA (not a library). It is served at /admin — both by the Vite dev
// server and, in an integrated host, by Cortex.AspNetCore's UseCortexAdminConsole — so `base` is "/admin/".
// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  base: "/admin/",
  resolve: {
    alias: {
      // Resolve the platform client (api, hooks, types) straight from @abrahamferga/cortex-ui source. This lets the
      // console build without a prior `@abrahamferga/cortex-ui` library build.
      "@abrahamferga/cortex-ui": fileURLToPath(new URL("../cortex-ui/src/index.ts", import.meta.url)),
    },
    // The aliased @abrahamferga/cortex-ui source lives under cortex-ui/, which has its own node_modules. Under pnpm's
    // isolated layout that would pull a second copy of React (→ "invalid hook call"). Dedupe to one copy.
    dedupe: ["react", "react-dom", "react-router-dom", "@tanstack/react-query"],
  },
  server: {
    port: 5174,
  },
  build: {
    outDir: "dist",
  },
});
