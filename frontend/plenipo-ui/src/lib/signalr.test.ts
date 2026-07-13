import { describe, expect, it } from "vitest";
import { agentHubUrl } from "./signalr";

describe("agentHubUrl", () => {
  it("targets the agent hub and forwards dev-auth as query params (the WebSocket handshake can't set headers)", () => {
    const url = new URL(agentHubUrl());

    expect(url.pathname).toBe("/hubs/agent");
    expect(url.searchParams.get("X-Dev-Subject")).toBe("dev-user");
    expect(url.searchParams.get("X-Dev-Tenant")).toBe("dev");
    expect(url.searchParams.get("X-Dev-Roles")).toBe("system_admin");
  });
});
