import { test, expect, type Page } from "@playwright/test";

/**
 * Issue #71's acceptance test, in a real browser: with an authority configured, the shell must be able
 * to complete a sign-in and must send NO `X-Dev-*` header on any request.
 *
 * The authority is a stub fulfilled at the network layer — discovery, authorize and token — so the whole
 * PKCE round trip runs for real against a fake IdP with no backend and no IdP to stand up.
 */

const AUTHORITY = "https://idp.example.test/tenant";
const CLIENT_ID = "spa-client";

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

/** Every request the page made, so the "no dev headers, ever" assertion covers the whole run. */
function recordRequests(page: Page) {
  const devHeaderRequests: string[] = [];
  const authorized: string[] = [];
  page.on("request", (request) => {
    const headers = request.headers();
    if (Object.keys(headers).some((key) => /^x-dev-/i.test(key))) {
      devHeaderRequests.push(request.url());
    }
    if (headers["authorization"]?.startsWith("Bearer ")) {
      authorized.push(request.url());
    }
  });
  return { devHeaderRequests, authorized };
}

async function stubAuthority(page: Page, opts: { withToken?: boolean } = {}) {
  await page.route("**/api/platform/auth-config", (route) =>
    route.fulfill({ json: { mode: "oidc", authority: AUTHORITY, clientId: CLIENT_ID, scopes: null } }),
  );

  await page.route(`${AUTHORITY}/.well-known/openid-configuration`, (route) =>
    route.fulfill({
      json: {
        authorization_endpoint: `${AUTHORITY}/authorize`,
        token_endpoint: `${AUTHORITY}/token`,
        end_session_endpoint: `${AUTHORITY}/logout`,
      },
    }),
  );

  // The authority "signs the user in" by bouncing straight back to the callback with a code.
  await page.route(`${AUTHORITY}/authorize**`, (route) => {
    const url = new URL(route.request().url());
    const redirect = url.searchParams.get("redirect_uri")!;
    const state = url.searchParams.get("state")!;
    return route.fulfill({
      status: 302,
      headers: { location: `${redirect}?code=test-code&state=${encodeURIComponent(state)}` },
      body: "",
    });
  });

  await page.route(`${AUTHORITY}/token`, (route) =>
    route.fulfill({
      json: opts.withToken === false
        ? { error: "invalid_grant" }
        : { access_token: "stub-access-token", refresh_token: "stub-refresh", expires_in: 3600 },
    }),
  );
}

/** The API accepts the stub bearer and refuses anything else — so a missing token really shows up. */
async function stubApi(page: Page) {
  const authorized = (route: Parameters<Parameters<Page["route"]>[1]>[0], json: unknown) => {
    const auth = route.request().headers()["authorization"];
    return auth === "Bearer stub-access-token"
      ? route.fulfill({ json })
      : route.fulfill({ status: 401, json: { title: "Unauthorized" } });
  };

  await page.route("**/api/platform/modules", (route) => authorized(route, MODULES));
  await page.route("**/api/platform/me", (route) =>
    authorized(route, {
      userId: "u",
      subject: "stub-subject",
      displayName: "Signed In",
      tenantId: "t",
      permissions: ["*"],
      tenantResolved: true,
      tenantProblem: null,
    }),
  );
  await page.route("**/api/platform/info", (route) =>
    route.fulfill({ json: { chatEnabled: false, demoMode: false } }),
  );
  await page.route("**/api/platform/branding", (route) => route.fulfill({ json: { name: "Plenipo" } }));
  await page.route("**/api/demo/items", (route) => authorized(route, [{ name: "Widget" }]));
}

test("signs in against a stub authority and never sends a dev-auth header", async ({ page }) => {
  const seen = recordRequests(page);
  await stubAuthority(page);
  await stubApi(page);

  await page.goto("/");

  // Unauthenticated: the API refuses, and the shell offers the control that can fix it — where it used
  // to tell the user to "sign in and try again" beside a button that only retried.
  const signIn = page.getByRole("button", { name: "Sign in" });
  await expect(signIn).toBeVisible();

  await signIn.click();

  // Redirect → stub authority → callback → code exchange → the shell retries with a real bearer.
  await expect(page.getByRole("link", { name: "Items" })).toBeVisible({ timeout: 15_000 });

  // The acceptance criterion, over every request of the whole run.
  expect(seen.devHeaderRequests).toEqual([]);
  expect(seen.authorized.some((url) => url.includes("/api/platform/me"))).toBe(true);

  // And the callback URL is not left in history for Back to replay its single-use code.
  expect(new URL(page.url()).pathname).not.toBe("/signin-callback");
});

test("a refused sign-in says so instead of looping", async ({ page }) => {
  const seen = recordRequests(page);
  await stubAuthority(page, { withToken: false });
  await stubApi(page);

  await page.goto("/");
  await page.getByRole("button", { name: "Sign in" }).click();

  // The token exchange fails; the shell must land somewhere a person can act on, still unauthenticated,
  // and still without ever falling back to dev headers.
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible({ timeout: 15_000 });
  expect(seen.devHeaderRequests).toEqual([]);
});

test("a dev-mode deployment keeps working with no sign-in at all", async ({ page }) => {
  // The keyless default must survive: a local host with no IdP still authenticates by header.
  await page.route("**/api/platform/auth-config", (route) => route.fulfill({ json: { mode: "dev" } }));
  await page.route("**/api/platform/modules", (route) => route.fulfill({ json: MODULES }));
  await page.route("**/api/platform/me", (route) =>
    route.fulfill({ json: { userId: "u", displayName: "Dev User", tenantId: "t", permissions: ["*"] } }),
  );
  await page.route("**/api/platform/info", (route) =>
    route.fulfill({ json: { chatEnabled: false, demoMode: false } }),
  );
  await page.route("**/api/demo/items", (route) => route.fulfill({ json: [{ name: "Widget" }] }));

  const seen = recordRequests(page);
  await page.goto("/");

  await expect(page.getByRole("link", { name: "Items" })).toBeVisible();
  expect(seen.devHeaderRequests.length).toBeGreaterThan(0);
});
