import { test, expect, type Page } from "@playwright/test";

// Real-browser proof for platform-request #112 (from abrahamFerga/networthy#152). The unit test in
// GenericTab.test.tsx guards the class contract; only a browser computes layout, so the claim that
// actually matters — the last column is REACHABLE — is proven here.
//
// The viewport stays at desktop width on purpose. `NARROW_QUERY` is viewport-width only, so below
// 768px the card layout takes over and there is no table to talk about. The case the request is
// about is a wrapper that narrows while the viewport does not: a table wider than its own container
// at a perfectly ordinary window size.

const COLUMNS = [
  "Posted date",
  "Description",
  "Counterparty",
  "Account",
  "Category",
  "Subcategory",
  "Reference",
  "Amount",
  "Balance",
  "Transfer",
];

const ROW: Record<string, string> = {
  postedDate: "2026-08-06",
  description: "Recurring transfer to joint savings account",
  counterparty: "Banco Santander S.A.",
  account: "Everyday current account",
  category: "Transfers between own accounts",
  subcategory: "Scheduled monthly",
  reference: "REF-2026-08-06-000184",
  amount: "1,715.50",
  balance: "12,884.21",
  transfer: "Yes — matched",
};

const field = (header: string) => {
  const [first, ...rest] = header.split(/[\s—]+/);
  return first.toLowerCase() + rest.map((w) => w[0].toUpperCase() + w.slice(1).toLowerCase()).join("");
};

const LEDGER = {
  id: "finance",
  displayName: "Finance",
  tabs: [
    {
      id: "ledger",
      label: "Ledger",
      route: "/finance/ledger",
      dataEndpoint: "/api/finance/ledger",
      columns: COLUMNS.map((header) => ({ field: field(header), header })),
    },
  ],
};

async function mock(page: Page) {
  await page.route("**/api/platform/modules", (r) => r.fulfill({ json: [LEDGER] }));
  await page.route("**/api/platform/me", (r) =>
    r.fulfill({ json: { userId: "u", displayName: "E2E User", tenantId: "t", permissions: ["*"] } }),
  );
  await page.route("**/api/platform/info", (r) => r.fulfill({ json: { chatEnabled: false, demoMode: false } }));
  await page.route("**/api/finance/ledger", (r) => r.fulfill({ json: [ROW] }));
}

test("a table wider than its wrapper scrolls across, and the last column is reachable", async ({ page }) => {
  // Wide enough that the card layout never engages, narrow enough that ten columns do not fit.
  await page.setViewportSize({ width: 900, height: 700 });
  await mock(page);
  await page.goto("/finance/ledger");

  const table = page.getByRole("table");
  await expect(table).toBeVisible();

  const wrapper = page.locator("table").locator("..");
  const lastHeader = page.getByRole("columnheader", { name: COLUMNS[COLUMNS.length - 1] });

  // The premise: the table really does overflow its wrapper. Without this the test proves nothing.
  const metrics = await wrapper.evaluate((el) => ({
    clientWidth: el.clientWidth,
    scrollWidth: el.scrollWidth,
    overflowX: getComputedStyle(el).overflowX,
    overflowY: getComputedStyle(el).overflowY,
  }));
  expect(metrics.scrollWidth).toBeGreaterThan(metrics.clientWidth);

  // 1. Scrollable across rather than clipping.
  expect(metrics.overflowX).toBe("auto");
  // 3. Still clipped down, so the rounded corners keep cropping the table's own edges.
  expect(metrics.overflowY).toBe("hidden");

  // The last column starts out beyond the wrapper's right edge — this is the reported symptom.
  const clippedAtRest = await lastHeader.evaluate(
    (cell, w) => cell.getBoundingClientRect().right > w.getBoundingClientRect().right + 1,
    await wrapper.elementHandle(),
  );
  expect(clippedAtRest).toBe(true);

  // 2. And it is reachable: scrolling the wrapper brings it fully inside.
  await wrapper.evaluate((el) => {
    el.scrollLeft = el.scrollWidth;
  });
  await expect
    .poll(async () =>
      lastHeader.evaluate(
        (cell, w) => {
          const c = cell.getBoundingClientRect();
          const b = w.getBoundingClientRect();
          return c.left >= b.left - 1 && c.right <= b.right + 1;
        },
        await wrapper.elementHandle(),
      ),
    )
    .toBe(true);
  await expect(lastHeader).toBeInViewport();

  // The wrapper absorbs the overflow, so the page itself never grows a horizontal scrollbar.
  const pageOverflows = await page.evaluate(
    () => document.documentElement.scrollWidth > document.documentElement.clientWidth,
  );
  expect(pageOverflows).toBe(false);
});
