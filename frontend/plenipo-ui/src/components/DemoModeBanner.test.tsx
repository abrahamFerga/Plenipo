// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { DemoModeBanner } from "./DemoModeBanner";

function renderBanner(demoMode: boolean) {
  vi.stubGlobal(
    "fetch",
    vi.fn(() =>
      Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ chatEnabled: true, demoMode }),
      } as unknown as Response),
    ),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <DemoModeBanner />
    </QueryClientProvider>,
  );
}

describe("DemoModeBanner", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("shows the demo-mode notice when the deployment runs the Mock provider", async () => {
    renderBanner(true);
    expect(await screen.findByText(/Demo mode/)).toBeTruthy();
  });

  it("stays hidden once a real AI provider is configured", async () => {
    renderBanner(false);
    // Wait for /info to resolve, then confirm the banner never appears.
    await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
    expect(screen.queryByText(/Demo mode/)).toBeNull();
  });
});
