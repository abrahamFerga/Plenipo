// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { clientConfig, resetClientConfig } from "@plenipo/client";
import {
  configurePlenipoWeb,
  devAuthOnly,
  memoryStorage,
  plenipoWebMode,
  resetPlenipoWeb,
  signOutPlenipoWeb,
  type AuthAdapter,
} from "./webAuth";

afterEach(() => {
  resetClientConfig();
  resetPlenipoWeb();
});

const noHeaders = (headers: Record<string, string>) =>
  Object.keys(headers).filter((key) => /^x-dev-/i.test(key));

describe("configurePlenipoWeb", () => {
  it("sends NO dev headers when a secured deployment has no token yet", async () => {
    // The headline bug this guards. Mirroring the mobile adapter line for line would fall back to the
    // dev-auth headers when the token is null — putting `X-Dev-Roles: system_admin` on the first request
    // of a secured deployment, before the sign-in button has even rendered. Inert against the server,
    // but a lie on the wire and the exact thing the issue asked never to happen.
    configurePlenipoWeb({ mode: "oidc", auth: { getAccessToken: async () => null } });

    const headers = await clientConfig().authHeaders();

    expect(headers).toEqual({});
    expect(noHeaders(headers)).toEqual([]);
  });

  it("still falls back to dev headers when the deployment has no IdP", async () => {
    configurePlenipoWeb({ mode: "dev", auth: devAuthOnly() });

    const headers = await clientConfig().authHeaders();

    expect(headers["X-Dev-Subject"]).toBe("dev-user");
  });

  it("sends a bearer and NO dev headers once a token exists", async () => {
    configurePlenipoWeb({ mode: "oidc", auth: { getAccessToken: async () => "tok-123" } });

    const headers = await clientConfig().authHeaders();

    expect(headers["Authorization"]).toBe("Bearer tok-123");
    expect(noHeaders(headers)).toEqual([]);
  });

  it("keeps using the stored token while the adapter is refreshing", async () => {
    const storage = memoryStorage();
    let token: string | null = "first";
    configurePlenipoWeb({ mode: "oidc", storage, auth: { getAccessToken: async () => token } });

    expect((await clientConfig().authHeaders())["Authorization"]).toBe("Bearer first");

    token = null; // mid-refresh
    expect((await clientConfig().authHeaders())["Authorization"]).toBe("Bearer first");
  });

  it("reports the mode synchronously, for callers that cannot await", () => {
    expect(plenipoWebMode()).toBe("dev");
    configurePlenipoWeb({ mode: "oidc" });
    expect(plenipoWebMode()).toBe("oidc");
  });
});

describe("signOutPlenipoWeb", () => {
  it("forgets the stored token as well as calling the adapter", async () => {
    // The mobile shell's signOut never cleared its stored token, so the next request re-authenticated
    // as the signed-out user. Mirroring it line for line would have copied that.
    const storage = memoryStorage();
    const signOut = vi.fn(async () => {});
    const auth: AuthAdapter = { getAccessToken: async () => "tok", signOut };
    configurePlenipoWeb({ mode: "oidc", storage, auth });

    await clientConfig().authHeaders(); // stores the token
    expect(await storage.getItem("plenipo.accessToken")).toBe("tok");

    await signOutPlenipoWeb();

    expect(signOut).toHaveBeenCalledOnce();
    expect(await storage.getItem("plenipo.accessToken")).toBeNull();
  });

  it("is safe when the adapter offers no signOut", async () => {
    configurePlenipoWeb({ mode: "dev", auth: devAuthOnly() });
    await expect(signOutPlenipoWeb()).resolves.toBeUndefined();
  });
});
