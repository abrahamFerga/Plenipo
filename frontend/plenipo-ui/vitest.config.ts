import { defineConfig } from "vitest/config";

// Standalone test config (kept separate from the library build in vite.config.ts). The unit tests cover
// pure logic — permission matching, the API client — so a lightweight Node environment is all they need.
export default defineConfig({
  test: {
    // Default to Node for fast pure-logic tests; component tests opt into jsdom with a
    // `// @vitest-environment jsdom` docblock at the top of the file.
    environment: "node",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
