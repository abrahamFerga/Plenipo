// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createOidcAuth, isSignInCallback, type OidcAuthAdapter } from "./oidc";

const AUTHORITY = "https://idp.example.test/tenant";
const DISCOVERY = {
  authorization_endpoint: `${AUTHORITY}/authorize`,
  token_endpoint: `${AUTHORITY}/token`,
  end_session_endpoint: `${AUTHORITY}/logout`,
};

function stubFetch(tokenResponse: Record<string, unknown> = { access_token: "at-1", expires_in: 3600 }) {
  const calls: { url: string; body?: string }[] = [];
  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, body: init?.body as string | undefined });
    if (url.includes("/.well-known/openid-configuration")) {
      return new Response(JSON.stringify(DISCOVERY), { status: 200 });
    }
    if (url === DISCOVERY.token_endpoint) {
      return new Response(JSON.stringify(tokenResponse), { status: 200 });
    }
    return new Response("not found", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return { calls, fetchMock };
}

function locationTo(url: string) {
  const assign = vi.fn();
  const parsed = new URL(url);
  vi.stubGlobal("location", {
    origin: parsed.origin,
    pathname: parsed.pathname,
    search: parsed.search,
    href: url,
    assign,
  });
  return assign;
}

beforeEach(() => {
  sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const adapter = () =>
  createOidcAuth({ authority: AUTHORITY, clientId: "spa-client" }) as OidcAuthAdapter;

describe("signIn", () => {
  it("redirects with an S256 PKCE challenge and a single-use state", async () => {
    stubFetch();
    const assign = locationTo("https://app.example.test/chat");

    await adapter().signIn!();

    const target = new URL(assign.mock.calls[0][0] as string);
    expect(target.origin + target.pathname).toBe(DISCOVERY.authorization_endpoint);
    expect(target.searchParams.get("response_type")).toBe("code");
    expect(target.searchParams.get("code_challenge_method")).toBe("S256");
    expect(target.searchParams.get("client_id")).toBe("spa-client");
    expect(target.searchParams.get("redirect_uri")).toBe("https://app.example.test/signin-callback");
    expect(target.searchParams.get("scope")).toContain("openid");

    // The verifier is kept for the exchange and the state for the match; both are single-use.
    const verifier = sessionStorage.getItem("plenipo.oidc.verifier")!;
    expect(verifier).toBeTruthy();
    expect(sessionStorage.getItem("plenipo.oidc.state")).toBe(target.searchParams.get("state"));

    // The challenge really is base64url(SHA-256(verifier)) — not the verifier itself.
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
    const expected = btoa(String.fromCharCode(...new Uint8Array(digest)))
      .replace(/\+/g, "-")
      .replace(/\//g, "_")
      .replace(/=+$/, "");
    expect(target.searchParams.get("code_challenge")).toBe(expected);
    expect(target.searchParams.get("code_challenge")).not.toBe(verifier);
  });

  it("remembers where the user was, so the callback can put them back", async () => {
    stubFetch();
    locationTo("https://app.example.test/modules/finance?tab=budgets");

    await adapter().signIn!();

    expect(sessionStorage.getItem("plenipo.oidc.return")).toBe("/modules/finance?tab=budgets");
  });
});

describe("completeSignIn", () => {
  it("exchanges the code with the verifier and no client secret", async () => {
    const { calls } = stubFetch({ access_token: "at-1", refresh_token: "rt-1", expires_in: 3600 });
    sessionStorage.setItem("plenipo.oidc.verifier", "verifier-abc");
    sessionStorage.setItem("plenipo.oidc.state", "state-xyz");
    sessionStorage.setItem("plenipo.oidc.return", "/chat");
    locationTo("https://app.example.test/signin-callback?code=the-code&state=state-xyz");

    const auth = adapter();
    expect(await auth.completeSignIn()).toBe("/chat");

    const exchange = calls.find((c) => c.url === DISCOVERY.token_endpoint)!;
    const body = new URLSearchParams(exchange.body!);
    expect(body.get("grant_type")).toBe("authorization_code");
    expect(body.get("code")).toBe("the-code");
    expect(body.get("code_verifier")).toBe("verifier-abc");
    expect(body.get("client_secret")).toBeNull();

    expect(await auth.getAccessToken()).toBe("at-1");
  });

  it("refuses a mismatched state and consumes the single-use values", async () => {
    stubFetch();
    sessionStorage.setItem("plenipo.oidc.verifier", "verifier-abc");
    sessionStorage.setItem("plenipo.oidc.state", "state-xyz");
    locationTo("https://app.example.test/signin-callback?code=the-code&state=ATTACKER");

    await expect(adapter().completeSignIn()).rejects.toThrow(/state did not match/i);

    // Consumed regardless, so replaying the same callback cannot mint a second session.
    expect(sessionStorage.getItem("plenipo.oidc.verifier")).toBeNull();
    expect(sessionStorage.getItem("plenipo.oidc.state")).toBeNull();
  });

  it("surfaces an authority error rather than pretending to be signed in", async () => {
    stubFetch();
    sessionStorage.setItem("plenipo.oidc.verifier", "v");
    sessionStorage.setItem("plenipo.oidc.state", "s");
    locationTo("https://app.example.test/signin-callback?error=access_denied&state=s");

    await expect(adapter().completeSignIn()).rejects.toThrow(/access_denied/);
  });
});

describe("getAccessToken", () => {
  it("returns null before any sign-in, so requests carry no credentials", async () => {
    stubFetch();
    locationTo("https://app.example.test/");

    expect(await adapter().getAccessToken()).toBeNull();
  });

  it("refreshes exactly once under concurrent callers", async () => {
    // Rotating refresh tokens make a burst of parallel refreshes actively harmful: each invalidates
    // the last, and the user is signed out. Ten components mounting at once must produce one exchange.
    const { calls } = stubFetch({ access_token: "at-2", refresh_token: "rt-2", expires_in: 3600 });
    sessionStorage.setItem("plenipo.oidc.refreshToken", "rt-1");
    locationTo("https://app.example.test/");

    const auth = adapter();
    const tokens = await Promise.all(Array.from({ length: 10 }, () => auth.getAccessToken()));

    expect(tokens.every((t) => t === "at-2")).toBe(true);
    expect(calls.filter((c) => c.url === DISCOVERY.token_endpoint)).toHaveLength(1);
  });

  it("treats an expired refresh token as a normal end of session", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) =>
        url.includes(".well-known")
          ? new Response(JSON.stringify(DISCOVERY), { status: 200 })
          : new Response("invalid_grant", { status: 400 }),
      ),
    );
    sessionStorage.setItem("plenipo.oidc.refreshToken", "stale");
    locationTo("https://app.example.test/");

    expect(await adapter().getAccessToken()).toBeNull();
    expect(sessionStorage.getItem("plenipo.oidc.refreshToken")).toBeNull();
  });
});

describe("isSignInCallback", () => {
  it("is true only on the redirect path carrying a code or an error", () => {
    locationTo("https://app.example.test/signin-callback?code=abc");
    expect(isSignInCallback()).toBe(true);

    locationTo("https://app.example.test/signin-callback");
    expect(isSignInCallback()).toBe(false);

    locationTo("https://app.example.test/chat?code=abc");
    expect(isSignInCallback()).toBe(false);
  });

  it("honours a per-surface redirect path", () => {
    locationTo("https://app.example.test/admin/signin-callback?code=abc");
    expect(isSignInCallback("/admin/signin-callback")).toBe(true);
    expect(isSignInCallback()).toBe(false);
  });
});
