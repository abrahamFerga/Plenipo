import { test, expect, type Page } from "@playwright/test";

// Chat E2E over a fully mocked API: the conversation list, resuming a conversation (history with
// attachment chips + markdown), and the composer's Shift+Enter behavior. SignalR streaming is out of
// scope — the default transport is AG-UI (HTTP per turn, nothing connects at mount); the hub route
// is still aborted defensively for any code path that opts into SignalR.
const MODULE = {
  id: "demo",
  displayName: "Demo Module",
  tabs: [],
};

const CONVERSATION = {
  id: "c-1",
  moduleId: "demo",
  title: "Contract review",
  updatedAt: "2026-06-30T12:00:00Z",
};

// The user turn carries the trailing "[Attached files]" block that withAttachmentRefs produces; the
// panel must parse it back into a chip instead of showing the raw text. The assistant turn carries
// markdown that must render formatted.
const MESSAGES = [
  {
    id: "m-1",
    role: "User",
    content: "Please review this document.\n\n[Attached files]\n- brief.pdf (file id: f-123)",
  },
  {
    id: "m-2",
    role: "Assistant",
    content: "Here is the **summary** you asked for.",
  },
];

async function mock(page: Page) {
  await page.route("**/api/platform/modules", (r) => r.fulfill({ json: [MODULE] }));
  await page.route("**/api/platform/me", (r) =>
    r.fulfill({ json: { userId: "u", displayName: "E2E User", tenantId: "t", permissions: ["*"] } }),
  );
  await page.route("**/api/platform/info", (r) => r.fulfill({ json: { chatEnabled: true, demoMode: false } }));
  await page.route("**/api/chat/approvals", (r) => r.fulfill({ json: [] }));
  // The single "*" never matches "/", so this catches the list (with its ?moduleId= query) but not
  // the /{id}/messages route below.
  await page.route("**/api/chat/conversations*", (r) => r.fulfill({ json: [CONVERSATION] }));
  await page.route(`**/api/chat/conversations/${CONVERSATION.id}/messages`, (r) =>
    r.fulfill({ json: MESSAGES }),
  );
  // Fail the agent-hub negotiate deterministically instead of letting it hit whatever (if anything)
  // listens on the API port. Streaming itself is not mocked — out of scope for this spec.
  await page.route("**/hubs/agent/**", (r) => r.abort());
}

test("renders the mocked conversation in the history sidebar", async ({ page }) => {
  await mock(page);
  await page.goto("/");

  // chatEnabled:true → the shell lands on /chat, where ChatView shows the conversation sidebar.
  await expect(page).toHaveURL(/\/chat$/);
  await expect(page.getByRole("button", { name: "+ New chat" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Contract review/ })).toBeVisible();
});

test("selecting a conversation renders history with an attachment chip and formatted markdown", async ({ page }) => {
  await mock(page);
  await page.goto("/");

  await page.getByRole("button", { name: /Contract review/ }).click();

  // The user turn shows its body with the "[Attached files]" block parsed out, not rendered raw.
  await expect(page.getByText("Please review this document.")).toBeVisible();
  await expect(page.getByText("[Attached files]")).toHaveCount(0);

  // The attachment renders as a chip: a download link to the file-store endpoint for f-123.
  const chip = page.locator('a[href*="/api/files/f-123"]');
  await expect(chip).toBeVisible();
  await expect(chip).toContainText("brief.pdf");

  // The assistant turn renders markdown formatted — **summary** becomes a <strong>, no literal markers.
  await expect(page.locator("strong", { hasText: "summary" })).toBeVisible();
  await expect(page.getByText("**summary**")).toHaveCount(0);
});

test("Shift+Enter inserts a newline in the composer without sending", async ({ page }) => {
  await mock(page);
  await page.goto("/");

  const composer = page.getByLabel("Message");
  await expect(composer).toBeVisible();
  // The default transport is AG-UI (plain HTTP per turn) — nothing connects at mount, so anything
  // recorded below can only come from the keypress.

  const sends: string[] = [];
  page.on("request", (req) => {
    if (req.method() !== "GET" || req.url().includes("/hubs/")) {
      sends.push(`${req.method()} ${req.url()}`);
    }
  });

  await composer.fill("hello");
  await composer.press("Shift+Enter");

  // Shift+Enter falls through to the textarea: a newline is inserted and the draft is kept.
  await expect(composer).toHaveValue("hello\n");
  // No turn started — the empty-state prompt is still showing instead of a message bubble.
  await expect(page.getByText("Start a conversation with the demo agent.")).toBeVisible();

  // Give any accidental send a beat to surface, then assert no POST/stream call ever fired.
  await page.waitForTimeout(300);
  expect(sends).toEqual([]);
});
