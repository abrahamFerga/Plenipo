// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { OnboardingWizard } from "./OnboardingWizard";
import type { Module } from "../lib/api";

const financeModule: Module = {
  id: "finance",
  displayName: "Finance",
  tabs: [],
  onboarding: {
    probeEndpoint: "/api/finance/accounts",
    title: "Set up your household finances",
    steps: [
      { id: "welcome", title: "Welcome", blurb: "What this is.", kind: "info" },
      {
        id: "accounts",
        title: "Add your accounts",
        blurb: "Where the money lives.",
        kind: "form",
        endpoint: "/api/finance/accounts",
        fields: [
          { field: "name", label: "Account name" },
          { field: "cachedBalance", label: "Balance", numeric: true },
        ],
        preset: { type: "checking", currencyCode: "USD" },
      },
      {
        id: "statements",
        title: "Upload statements",
        blurb: "Past expenses from your bank.",
        kind: "upload",
        endpoint: "/api/finance/imports",
        fileIdField: "fileId",
        accept: ".csv,.pdf",
        fields: [{ field: "accountName", label: "Account" }],
      },
    ],
  },
};

function renderWizard(fetchImpl: (url: string, init?: RequestInit) => Promise<unknown>) {
  vi.stubGlobal("fetch", vi.fn(fetchImpl));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onDone = vi.fn();
  render(
    <QueryClientProvider client={client}>
      <OnboardingWizard module={financeModule} onDone={onDone} />
    </QueryClientProvider>,
  );
  return onDone;
}

const okJson = (body: unknown) =>
  Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as unknown as Response);

describe("OnboardingWizard", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("walks info → form, posting preset + typed fields, and echoes what was added", async () => {
    const bodies: unknown[] = [];
    renderWizard((_url, init) => {
      if (init?.method === "POST") bodies.push(JSON.parse(String(init.body)));
      return okJson({});
    });

    // Info step → Continue.
    expect(screen.getByRole("heading", { name: "Welcome" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    // Form step: fill, add — preset merges into the body, numerics are numbers.
    fireEvent.change(screen.getByLabelText("Account name"), { target: { value: "Chase Checking" } });
    fireEvent.change(screen.getByLabelText("Balance"), { target: { value: "2500" } });
    fireEvent.click(screen.getByRole("button", { name: "Add" }));

    await waitFor(() => expect(screen.getByTestId("wizard-added").textContent).toContain("Chase Checking"));
    expect(bodies[0]).toEqual({ type: "checking", currencyCode: "USD", name: "Chase Checking", cachedBalance: 2500 });
    // The form resets for another entry.
    expect((screen.getByLabelText("Account name") as HTMLInputElement).value).toBe("");
    expect(screen.getByRole("button", { name: "Add another" })).toBeTruthy();
  });

  it("uploads a file then hands its stored id to the follow-up endpoint", async () => {
    const posts: { url: string; body: unknown }[] = [];
    renderWizard((url, init) => {
      if (String(url).includes("/api/files/")) return okJson({ id: "file-123", fileName: "jan.csv" });
      if (init?.method === "POST") {
        posts.push({ url: String(url), body: JSON.parse(String(init.body)) });
        return okJson({});
      }
      return okJson([]);
    });

    fireEvent.click(screen.getByRole("button", { name: "Continue" })); // past welcome
    fireEvent.click(screen.getByRole("button", { name: "Skip for now" })); // past accounts

    fireEvent.change(screen.getByLabelText("Account"), { target: { value: "Chase Checking" } });
    const input = document.querySelector('input[type="file"]')!;
    fireEvent.change(input, { target: { files: [new File(["Date,Amount"], "jan.csv", { type: "text/csv" })] } });

    await waitFor(() => expect(screen.getByTestId("wizard-uploads").textContent).toContain("jan.csv"));
    await waitFor(() => expect(posts.length).toBe(1));
    expect(posts[0].url).toContain("/api/finance/imports");
    expect(posts[0].body).toEqual({ accountName: "Chase Checking", fileId: "file-123" });
  });

  it("lets the user skip everything and finish — setup never traps", async () => {
    const onDone = renderWizard(() => okJson({}));

    fireEvent.click(screen.getByRole("button", { name: "Continue" }));
    fireEvent.click(screen.getByRole("button", { name: "Skip for now" }));
    fireEvent.click(screen.getByRole("button", { name: "Finish" }));

    expect(await screen.findByRole("heading", { name: "You're set up" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Go to Finance" }));
    expect(onDone).toHaveBeenCalled();
  });
});
