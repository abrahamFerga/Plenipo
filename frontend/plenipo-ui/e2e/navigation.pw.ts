import { test, expect, type Page } from "@playwright/test";

const DEMO = {
  id: "demo",
  displayName: "Demo Module",
  tabs: [
    { id: "items", label: "Items", route: "/demo/items", dataEndpoint: "/api/demo/items", columns: [{ field: "name", header: "Name" }] },
  ],
};
const OTHER = {
  id: "other",
  displayName: "Other Module",
  tabs: [
    { id: "widgets", label: "Widgets", route: "/other/widgets", dataEndpoint: "/api/other/widgets", columns: [{ field: "name", header: "Name" }] },
  ],
};

async function mock(page: Page, opts: { modules: unknown[]; chatEnabled: boolean }) {
  await page.route("**/api/platform/modules", (r) => r.fulfill({ json: opts.modules }));
  await page.route("**/api/platform/me", (r) =>
    r.fulfill({ json: { userId: "u", displayName: "E2E User", tenantId: "t", permissions: ["*"] } }),
  );
  await page.route("**/api/platform/info", (r) => r.fulfill({ json: { chatEnabled: opts.chatEnabled, demoMode: false } }));
  await page.route("**/api/demo/items", (r) => r.fulfill({ json: [{ name: "Widget" }] }));
  await page.route("**/api/other/widgets", (r) => r.fulfill({ json: [{ name: "Gizmo" }] }));
}

test("deep-links straight to a tab route even when chat is enabled (no redirect to /chat)", async ({ page }) => {
  await mock(page, { modules: [DEMO], chatEnabled: true });
  await page.goto("/demo/items");

  // The active module is resolved from the URL, so the tab renders instead of bouncing to chat.
  await expect(page.getByRole("heading", { name: "Items" })).toBeVisible();
  await expect(page.getByText("Widget")).toBeVisible();
});

test("switches the active module from the top-bar switcher", async ({ page }) => {
  await mock(page, { modules: [DEMO, OTHER], chatEnabled: false });
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Items" })).toBeVisible(); // first module lands

  // By role: getByLabel("Module") would also substring-match the sidebar nav's "Module tabs"
  // aria-label; the module switcher is the shell's only combobox.
  await page.getByRole("combobox").selectOption({ label: "Other Module" });

  // Switching navigates to the other module's first tab and renders its data.
  await expect(page.getByRole("heading", { name: "Widgets" })).toBeVisible();
  await expect(page.getByText("Gizmo")).toBeVisible();
});
