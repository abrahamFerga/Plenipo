// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { PermissionGate } from "./PermissionGate";

function stubMe(permissions: string[]) {
  vi.stubGlobal(
    "fetch",
    vi.fn(() =>
      Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve({ userId: "u", displayName: "Dev", tenantId: "t", permissions }),
      } as unknown as Response),
    ),
  );
}

function renderGate(permission: string, fallback?: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <PermissionGate permission={permission} fallback={fallback}>
        <div>secret content</div>
      </PermissionGate>
    </QueryClientProvider>,
  );
}

describe("PermissionGate", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders its children once the user is known to hold the permission", async () => {
    stubMe(["platform.users.manage"]);
    renderGate("platform.users.manage");
    expect(await screen.findByText("secret content")).toBeTruthy();
  });

  it("honours wildcard grants (system_admin's *)", async () => {
    stubMe(["*"]);
    renderGate("anything.at.all");
    expect(await screen.findByText("secret content")).toBeTruthy();
  });

  it("renders the fallback and hides the children when the user lacks the permission", async () => {
    stubMe([]);
    renderGate("platform.users.manage", <div>not allowed</div>);
    expect(await screen.findByText("not allowed")).toBeTruthy();
    expect(screen.queryByText("secret content")).toBeNull();
  });
});
