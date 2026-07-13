import { test, expect, type Page } from "@playwright/test";

// A minimal security catalog so the default (Security) view renders without a backend.
const CATALOG = {
  platform: [
    {
      permission: "platform.users.manage",
      category: "Users",
      description: "Manage users",
      requiresApproval: false,
      audited: true,
    },
  ],
  modules: [
    {
      id: "finance",
      displayName: "Finance",
      tools: [
        {
          permission: "tools.finance.categorize",
          category: "finance",
          description: "Categorize a transaction",
          requiresApproval: false,
          audited: true,
        },
      ],
    },
  ],
};

async function mockApi(page: Page, permissions: string[]) {
  await page.route("**/api/platform/me", (route) =>
    route.fulfill({ json: { userId: "u", displayName: "Admin User", tenantId: "t", permissions } }),
  );
  await page.route("**/api/admin/security/catalog", (route) => route.fulfill({ json: CATALOG }));
}

test("an administrator sees the console and its administration sections", async ({ page }) => {
  await mockApi(page, ["*"]);
  await page.goto("/admin/");

  await expect(page.getByText("Admin", { exact: true })).toBeVisible(); // the console badge
  await expect(page.getByRole("link", { name: "Users" })).toBeVisible(); // a section in the left nav
  await expect(page.getByRole("link", { name: "Audit Log" })).toBeVisible();

  // Accessibility parity with the domain shell: skip link + a labelled main landmark.
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeAttached();
  await expect(page.getByRole("main", { name: "Administration" })).toBeVisible();
});

test("a non-administrator is refused", async ({ page }) => {
  await mockApi(page, []);
  await page.goto("/admin/");

  await expect(
    page.getByText("You do not have permission to administer this Plenipo instance."),
  ).toBeVisible();
});
