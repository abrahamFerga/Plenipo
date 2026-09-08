import { test, expect, type Page } from "@playwright/test";

// The core loop a product's user runs, in a real browser, against the real sample host with the
// Mock provider (#218). Every assertion below was first observed by hand on 2026-09-08; the point
// of writing it down is that a shell change which renders fine against the mocked API (e2e/) and
// breaks against the host — a bundled React runtime, a DTO shape, an SSE/SignalR framing change, a
// CSP refusal of the served shell's scripts — fails here, on the platform, before a product sees it.
//
// Serial and single-worker on purpose: the turns share one tenant and one conversation store, and
// the starter prompts only show on an empty conversation.

function collectErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(`pageerror: ${error.message}`));
  page.on("console", (message) => {
    if (message.type() === "error") {
      errors.push(`console.error: ${message.text()}`);
    }
  });
  return errors;
}

test("the served shell renders the real manifest, chats through the Mock, parks a write, releases it and shows the row", async ({ page }) => {
  const errors = collectErrors(page);

  // 1. The shell the host serves boots from the real manifest.
  await page.goto("/");
  await expect(page.getByRole("link", { name: "Transactions" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Budgets" })).toBeVisible();
  await expect(page.getByRole("combobox", { name: "Module" })).toHaveValue("finance");

  // 2. A read turn: the Mock routes the starter prompt to summarize_spending, the reply streams
  //    with the tool chip and the metered token count.
  // (A host whose chat cannot work — no provider, chat disabled — shows no starter prompts, and this
  // is where that reads as a clear assertion rather than a click that times out.)
  await expect(page.getByRole("button", { name: "Summarize my spending" })).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "Summarize my spending" }).click();
  await expect(page.getByText(/summarize_spending/).first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/\d+ tokens/).first()).toBeVisible({ timeout: 30_000 });

  // 3. A write turn parks: the tool is approval-gated, the assistant says so, and the approval card
  //    appears with the recorded arguments.
  await page.getByRole("textbox", { name: "Message" }).fill("Record a 250 MXN dinner expense");
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByText(/Approval required/)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/was not executed|Not done/).first()).toBeVisible();

  // 4. Approving releases it, once, and the outcome comes back into the conversation.
  await page.getByRole("button", { name: "Approve" }).click();
  await expect(page.getByText(/'record_transaction' ran/)).toBeVisible({ timeout: 30_000 });

  // 5. The write is real: the server-driven data tab lists the new row.
  await page.getByRole("link", { name: "Transactions" }).click();
  await expect(page.getByRole("cell", { name: "Record a 250 MXN dinner expense" })).toBeVisible({ timeout: 15_000 });

  expect(errors, "the served shell ran with no page or console errors").toEqual([]);
});
