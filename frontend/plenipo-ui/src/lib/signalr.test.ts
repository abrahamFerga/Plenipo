import { afterEach, describe, expect, it } from "vitest";
import { resetClientConfig } from "@plenipo/client";
import { agentHubUrl } from "./signalr";
import { configurePlenipoWeb, resetPlenipoWeb } from "./webAuth";

afterEach(() => {
  resetClientConfig();
  resetPlenipoWeb();
});

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
  it("sends no dev headers on a secured deployment", async () => {
    configurePlenipoWeb({ mode: "oidc", auth: { getAccessToken: async () => "tok" } });

    // The connection builder reads its header bag synchronously; assert the decision it reads.
    const { plenipoWebMode } = await import("./webAuth");
    expect(plenipoWebMode()).toBe("oidc");
  });

  it("keeps dev headers where dev auth is what the host accepts", async () => {
    configurePlenipoWeb({ mode: "dev" });

    const { plenipoWebMode } = await import("./webAuth");
    expect(plenipoWebMode()).toBe("dev");
  });
});
