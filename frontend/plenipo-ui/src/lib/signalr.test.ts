import { afterEach, describe, expect, it, vi } from "vitest";
import { configureClient, devAuthHeaders, resetClientConfig } from "@plenipo/client";

// Capture what the connection builder is handed, so the header bag the hub actually receives is
// what the assertions read — not a proxy for it.
const captured = vi.hoisted(() => ({ withUrl: [] as Array<{ url: string; options: Record<string, unknown> }> }));
vi.mock("@microsoft/signalr", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@microsoft/signalr")>();
  class CapturingBuilder {
    withUrl(url: string, options: Record<string, unknown>) {
      captured.withUrl.push({ url, options });
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return { start: async () => {}, stop: async () => {} } as unknown;
    }
  }
  return { ...actual, HubConnectionBuilder: CapturingBuilder };
});

import { agentHubUrl, createAgentConnection } from "./signalr";
import { configurePlenipoWeb, resetPlenipoWeb } from "./webAuth";

afterEach(() => {
  resetClientConfig();
  resetPlenipoWeb();
  captured.withUrl.length = 0;
});

function hubHeaders(): Record<string, string> {
  createAgentConnection();
  const last = captured.withUrl[captured.withUrl.length - 1];
  if (!last) throw new Error("withUrl was not called");
  return (last.options.headers ?? {}) as Record<string, string>;
}

describe("agentHubUrl", () => {
  it("carries no credentials in the URL", () => {
    // It used to append the dev-auth headers as query parameters, justified by a comment claiming
    // "the server reads either". Nothing in the platform reads Request.Query for identity, so they
    // authenticated nothing — while putting `X-Dev-Roles: system_admin` into browser history, proxy
    // logs and error reports of any deployment that used them.
    const url = new URL(agentHubUrl());
    expect(url.pathname).toBe("/hubs/agent");
    expect([...url.searchParams.keys()]).toEqual([]);
  });
});

describe("createAgentConnection", () => {
  it("sends no dev headers on a secured deployment", () => {
    configurePlenipoWeb({ mode: "oidc", auth: { getAccessToken: async () => "tok" } });
    expect(hubHeaders()).toEqual({});
  });

  it("keeps the shared dev identity where nothing else was configured", () => {
    configurePlenipoWeb({ mode: "dev" });
    expect(hubHeaders()).toEqual(devAuthHeaders);
  });

  it("carries the configured client's dev identity onto the hub, not the shared constant (#215)", () => {
    // A product's dev-identity picker configures the client once; every HTTP request then carries
    // that identity. The hub must carry the same one, or chat turns run as the constant's
    // system_admin while the REST calls run as whoever was picked — the split identity #110 fixed
    // on the server, recreated in the client.
    configurePlenipoWeb({ mode: "dev" });
    configureClient({
      authHeaders: () => ({ "X-Dev-Subject": "alice", "X-Dev-Tenant": "acme", "X-Dev-Roles": "household-member" }),
    });
    expect(hubHeaders()).toEqual({ "X-Dev-Subject": "alice", "X-Dev-Tenant": "acme", "X-Dev-Roles": "household-member" });
  });

  it("never puts a bearer into the header bag — that travels through accessTokenFactory", () => {
    configurePlenipoWeb({ mode: "dev" });
    configureClient({ authHeaders: () => ({ Authorization: "Bearer t", "X-Dev-Subject": "alice" }) });
    expect(hubHeaders()).toEqual({ "X-Dev-Subject": "alice" });
  });

  it("falls back to the shared dev identity when the configured headers are async", () => {
    // withUrl reads its header bag synchronously; a promise cannot be unwrapped there. The constant
    // is the documented fallback, so a product whose picker is async knows to make it synchronous.
    configurePlenipoWeb({ mode: "dev" });
    configureClient({ authHeaders: async () => ({ "X-Dev-Subject": "later" }) });
    expect(hubHeaders()).toEqual(devAuthHeaders);
  });
});
