import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Two build targets share this config:
//  - default (`pnpm build`): the LIBRARY (@plenipo/ui's npm surface — src/index.ts, peer deps external)
//  - `pnpm build:app` (--mode app): the standalone APP SHELL (index.html entry, everything
//    bundled) that a product host serves from wwwroot/app (see Plenipo.AspNetCore's
//    UsePlenipoDomainUi). Branding and API base bake in via VITE_BRAND_NAME / VITE_API_BASE.
// The dev server always runs the app shell.
// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const appBuild = mode === "app";

  // Default the brand so index.html's %VITE_BRAND_NAME% never renders as a literal placeholder.
  process.env.VITE_BRAND_NAME ??= "Plenipo";

  return {
    plugins: [react()],

    build: appBuild
      ? {
          outDir: "dist-app",
        }
      : {
          lib: {
            // Library entry: exports all public components and hooks.
            entry: "src/index.ts",
            name: "PlenipoUI",
            fileName: (format) => `plenipo-ui.${format}.js`,
          },
          rollupOptions: {
            // Peer dependencies — consuming apps supply these.
            external: [
              "react",
              "react-dom",
              "react-router-dom",
              "@microsoft/signalr",
              "@tanstack/react-query",
            ],
            output: {
              globals: {
                react: "React",
                "react-dom": "ReactDOM",
                "react-router-dom": "ReactRouterDOM",
                "@microsoft/signalr": "signalR",
                "@tanstack/react-query": "ReactQuery",
              },
            },
          },
        },
  };
});
