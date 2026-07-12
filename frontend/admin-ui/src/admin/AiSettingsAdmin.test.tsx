// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AiSettingsAdmin } from "./AiSettingsAdmin";

const SETTINGS = {
  systemPromptOverride: null,
  maxConversationTokensOverride: null,
  maxMonthlyTokensOverride: null,
  providerOverride: null,
  modelOverride: null,
  endpointOverride: null,
  hasApiKey: false,
  defaultSystemPrompt: "You are Cortex.",
  defaultMaxConversationTokens: 0,
  defaultMaxMonthlyTokens: 0,
  defaultProvider: "Mock",
  defaultModel: "gpt-4o-mini",
};

function stubApi() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/admin/ai-settings") && method === "GET") {
      return Promise.resolve({ ok: true, json: () => Promise.resolve(SETTINGS) } as unknown as Response);
    }
    if (url.includes("/api/admin/ai-models") && method === "POST") {
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ models: ["provider-model-a", "provider-model-b"], supportsDiscovery: true }),
      } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderSettings() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <AiSettingsAdmin />
    </QueryClientProvider>,
  );
}

describe("AiSettingsAdmin", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("shows the deployment default as the system-prompt placeholder", async () => {
    stubApi();
    renderSettings();
    const textarea = (await screen.findByLabelText("System prompt")) as HTMLTextAreaElement;
    expect(textarea.placeholder).toContain("You are Cortex.");
  });

  it("saves the tenant overrides as a PUT (blank system prompt ⇒ null)", async () => {
    const fetchMock = stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    fireEvent.change(screen.getByLabelText("Conversation token budget"), { target: { value: "5000" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) => String(c[0]).includes("/api/admin/ai-settings") && (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      expect(put).toBeTruthy();
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({
        systemPrompt: null,
        maxConversationTokens: 5000,
        maxMonthlyTokens: null,
        provider: null,
        model: null,
        endpoint: null,
        apiKey: null,
      });
    });
  });

  it("rejects a non-numeric token budget (Save disabled, no PUT)", async () => {
    const fetchMock = stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    fireEvent.change(screen.getByLabelText("Conversation token budget"), { target: { value: "lots" } });

    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(fetchMock.mock.calls.some((c) => (c[1] as RequestInit | undefined)?.method === "PUT")).toBe(false);
  });

  it("loads the selected provider's live models without hardcoding options", async () => {
    const fetchMock = stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "OpenAI" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-test" } });
    fireEvent.click(screen.getByRole("button", { name: "Load models from provider" }));

    await screen.findByText(/2 models loaded/);
    const post = fetchMock.mock.calls.find(
      (c) => String(c[0]).includes("/api/admin/ai-models") && (c[1] as RequestInit | undefined)?.method === "POST",
    );
    expect(JSON.parse((post![1] as RequestInit).body as string)).toEqual({
      provider: "OpenAI",
      endpoint: null,
      apiKey: "sk-test",
    });
    expect(document.querySelector('option[value="provider-model-a"]')).not.toBeNull();
  });
});
