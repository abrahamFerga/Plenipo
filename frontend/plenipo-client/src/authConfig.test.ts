import { afterEach, describe, expect, it } from "vitest";
import { configureClient, resetClientConfig } from "./config";
import { fetchAuthConfig } from "./api";

afterEach(() => resetClientConfig());

function stubFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const calls: { url: string; init?: RequestInit }[] = [];
  configureClient({
    baseUrl: "https://api.example.test",
    fetch: async (url, init) => {
      calls.push({ url, init });
      return handler(url, init);
    },
  });
  return calls;
}

describe("fetchAuthConfig", () => {
  it("sends NO credentials — it is the probe that decides what the credentials should be", async () => {
    // Routing this through the normal request path would attach the configured auth headers, which on a
    // fresh start are the dev-auth headers. The very request that asks "does this deployment use dev
    // auth?" would then assert dev auth — on a secured deployment, before anything knew better.
    const calls = stubFetch(() => new Response(JSON.stringify({ mode: "dev" }), { status: 200 }));

    await fetchAuthConfig();

    const headers = (calls[0].init?.headers ?? {}) as Record<string, string>;
    expect(Object.keys(headers).filter((k) => /^x-dev-/i.test(k))).toEqual([]);
    expect(headers["Authorization"]).toBeUndefined();
  });

  it("asks the configured host", async () => {
    const calls = stubFetch(() => new Response(JSON.stringify({ mode: "dev" }), { status: 200 }));

    await fetchAuthConfig();

    expect(calls[0].url).toBe("https://api.example.test/api/platform/auth-config");
  });

  it("returns the published OIDC metadata", async () => {
    stubFetch(() =>
      new Response(
        JSON.stringify({ mode: "oidc", authority: "https://idp.test", clientId: "spa", scopes: null }),
        { status: 200 },
      ),
    );

    await expect(fetchAuthConfig()).resolves.toMatchObject({
      mode: "oidc",
      authority: "https://idp.test",
      clientId: "spa",
    });
  });

  it("answers dev when the host is too old to serve the endpoint", async () => {
    // A 404 is what an older host returns. Falling back to dev is the historical behaviour, and the
    // only answer that keeps a local no-IdP setup working with nothing configured.
    stubFetch(() => new Response("not found", { status: 404 }));

    await expect(fetchAuthConfig()).resolves.toEqual({ mode: "dev" });
  });

  it("answers dev rather than throwing when the host is unreachable", async () => {
    configureClient({
      baseUrl: "https://api.example.test",
      fetch: () => Promise.reject(new Error("ECONNREFUSED")),
    });

    await expect(fetchAuthConfig()).resolves.toEqual({ mode: "dev" });
  });

  it("treats an unrecognised mode as dev rather than half-configuring a sign-in", async () => {
    stubFetch(() => new Response(JSON.stringify({ authority: "https://idp.test" }), { status: 200 }));

    await expect(fetchAuthConfig()).resolves.toEqual({ mode: "dev" });
  });

  it("accepts an explicit base, for a shell configured later", async () => {
    const calls = stubFetch(() => new Response(JSON.stringify({ mode: "dev" }), { status: 200 }));

    await fetchAuthConfig("https://other.example.test/");

    expect(calls[0].url).toBe("https://other.example.test/api/platform/auth-config");
  });
});

describe("configureClient", () => {
  it("lets a bearer replace the dev headers entirely", async () => {
    // The existing suite proved the override is honoured; it never asserted the ABSENCE of the dev
    // headers, which is the product's actual criterion.
    const captured: Record<string, string>[] = [];
    configureClient({
      baseUrl: "https://api.example.test",
      authHeaders: async () => ({ Authorization: "Bearer t" }),
      fetch: async (_url, init) => {
        captured.push((init?.headers ?? {}) as Record<string, string>);
        return new Response("[]", { status: 200 });
      },
    });

    const { apiGet } = await import("./api");
    await apiGet("/api/platform/modules");

    expect(captured[0]["Authorization"]).toBe("Bearer t");
    expect(Object.keys(captured[0]).filter((k) => /^x-dev-/i.test(k))).toEqual([]);
  });
});
