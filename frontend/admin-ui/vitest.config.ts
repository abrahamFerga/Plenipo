import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";

// Component tests for the admin console. Resolves @abrahamferga/cortex-ui from source (mirroring vite.config) and dedupes
// React so the shared client and the test renderer use one copy. All tests are component tests, so jsdom.
export default defineConfig({
  resolve: {
    alias: {
      "@abrahamferga/cortex-ui": fileURLToPath(new URL("../cortex-ui/src/index.ts", import.meta.url)),
    },
    dedupe: ["react", "react-dom", "@tanstack/react-query"],
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
