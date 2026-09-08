import { defineConfig, devices } from "@playwright/test";

// Real-browser E2E against the REAL sample host (#218): the app shell the host serves from
// wwwroot/app, the real /api/platform/modules, real SignalR, the Mock provider — nothing mocked at
// the network layer. The mocked specs under e2e/ prove the shell's own logic; these prove the wire
// between shell and host, which is where a bundled runtime (#213), a DTO shape, an SSE framing or
// a CSP refusal breaks. eng/browser-e2e.sh builds the shell, starts the host on a throwaway Postgres
// and runs this config; PLENIPO_HOST_URL points at whatever host is up.
export default defineConfig({
  testDir: "./e2e-host",
  testMatch: "**/*.host.pw.ts",
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  reporter: "list",
  use: {
    baseURL: process.env.PLENIPO_HOST_URL ?? "http://127.0.0.1:8080",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
