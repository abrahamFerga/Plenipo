import { defineConfig, devices } from "@playwright/test";

// Real-browser E2E for the domain shell. The platform API is mocked at the network layer (see the specs
// under e2e/), so no backend is required — Playwright starts the Vite dev server itself. Spec files use a
// `.pw.ts` suffix so the vitest unit runner (which matches *.test/*.spec) never picks them up.
export default defineConfig({
  testDir: "./e2e",
  testMatch: "**/*.pw.ts",
  fullyParallel: true,
  reporter: "list",
  use: {
    baseURL: "http://localhost:5173",
    trace: "on-first-retry",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
  webServer: {
    command: "npx vite --port 5173",
    url: "http://localhost:5173",
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
