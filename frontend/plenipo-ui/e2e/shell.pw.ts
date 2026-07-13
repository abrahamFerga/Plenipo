import { test, expect } from "@playwright/test";

// A minimal server-driven manifest: one module with a data-endpoint tab. chatEnabled:false makes the
// shell land on the module's first tab (no chat/SignalR to stand up), where GenericTab renders the data.
const MODULES = [
  {
    id: "demo",
    displayName: "Demo Module",
    tabs: [
      {
        id: "items",
        label: "Items",
        route: "/demo/items",
        dataEndpoint: "/api/demo/items",
        columns: [{ field: "name", header: "Name" }],
      },
    ],
  },
];

test.beforeEach(async ({ page }) => {
  // Mock the platform API so the E2E needs no backend (glob matches whatever host VITE_API_BASE points at).
  await page.route("**/api/platform/modules", (route) => route.fulfill({ json: MODULES }));
  await page.route("**/api/platform/me", (route) =>
    route.fulfill({ json: { userId: "u", displayName: "E2E User", tenantId: "t", permissions: ["*"] } }),
  );
  await page.route("**/api/platform/info", (route) =>
    route.fulfill({ json: { chatEnabled: false, demoMode: false } }),
  );
  await page.route("**/api/demo/items", (route) =>
    route.fulfill({ json: [{ name: "Widget" }, { name: "Gadget" }] }),
  );
});

test("boots the shell and renders a module's server-driven tab", async ({ page }) => {
  await page.goto("/");

  // Brand in the top bar and the module's tab in the sidebar nav.
  await expect(page.getByText("Plenipo")).toBeVisible();
  await expect(page.getByRole("link", { name: "Items" })).toBeVisible();

  // chatEnabled:false → the default landing is the module's first tab; GenericTab renders the mocked rows.
  await expect(page.getByRole("heading", { name: "Items" })).toBeVisible();
  await expect(page.getByText("Widget")).toBeVisible();
  await expect(page.getByText("Gadget")).toBeVisible();
});

test("has an accessible skip-to-content link", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeAttached();
});
