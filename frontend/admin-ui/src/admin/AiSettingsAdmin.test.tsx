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
  defaultSystemPrompt: "You are Plenipo.",
  defaultMaxConversationTokens: 0,
  defaultMaxMonthlyTokens: 0,
  defaultProvider: "Mock",
  defaultModel: "gpt-4o-mini",
  agentSecurityModeOverride: null,
  promptAttackDetectionEnabledOverride: null,
  contentSafetyEnabledOverride: null,
  sensitiveDataHandlingOverride: null,
  defaultAgentSecurityMode: "Disabled",
  defaultPromptAttackDetectionEnabled: false,
  defaultContentSafetyEnabled: false,
  defaultSensitiveDataHandling: "Disabled",
  harmfulContentDetectionConfigured: true,
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
    expect(textarea.placeholder).toContain("You are Plenipo.");
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
        agentSecurityMode: null,
        promptAttackDetectionEnabled: null,
        contentSafetyEnabled: null,
        sensitiveDataHandling: null,
      });
    });
  });

  it("saves staged agent-security controls", async () => {
    const fetchMock = stubApi();
    renderSettings();

    await screen.findByLabelText("Security enforcement");
    fireEvent.change(screen.getByLabelText("Security enforcement"), { target: { value: "Enforce" } });
    fireEvent.change(screen.getByLabelText("Prompt-attack detection"), { target: { value: "true" } });
    fireEvent.change(screen.getByLabelText("Harmful-content screening"), { target: { value: "true" } });
    fireEvent.change(screen.getByLabelText("Sensitive data"), { target: { value: "Redact" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) => String(c[0]).includes("/api/admin/ai-settings") && (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      const body = JSON.parse((put![1] as RequestInit).body as string);
      expect(body).toMatchObject({
        agentSecurityMode: "Enforce",
        promptAttackDetectionEnabled: true,
        contentSafetyEnabled: true,
        sensitiveDataHandling: "Redact",
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

  it("will not submit a provider that needs its own model without one", async () => {
    // The trap this closes: the form used to promise "Default: gpt-4o-mini" for every provider and
    // let you save it, and the server then refused the whole save. It refuses correctly — a model
    // belongs to a connection, and the deployment default is an id for a DIFFERENT provider (a
    // deployment may not default to OpenAI at all). So the form must not offer it.
    const fetchMock = stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "OpenAI" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-test" } });

    // Save is blocked, and says why rather than failing at the server.
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/needs an explicit model/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    expect(
      fetchMock.mock.calls.some(
        (c) => String(c[0]).includes("/api/admin/ai-settings") && (c[1] as RequestInit | undefined)?.method === "PUT",
      ),
    ).toBe(false);

    // Naming a model unblocks it.
    fireEvent.change(screen.getByLabelText("Model"), { target: { value: "gpt-4o" } });
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("still offers the deployment default model for a provider that can inherit it", async () => {
    // Mock and the "deployment default" provider option are not tenant-owned connections, so
    // inheriting the deployment's model is meaningful there and stays on offer.
    stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "Mock" } });

    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByText(/needs an explicit model/)).toBeNull();
    expect((screen.getByLabelText("Model") as HTMLInputElement).placeholder).toBe("Default: gpt-4o-mini");
  });

  it("turns the Model field into a dropdown once models are loaded, with a manual-entry escape", async () => {
    stubApi();
    renderSettings();

    await screen.findByLabelText("System prompt");
    // Before discovery the Model field is a free-text input (custom ids, Azure deployment names).
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "OpenAI" } });
    fireEvent.change(screen.getByLabelText("API key"), { target: { value: "sk-test" } });
    expect((screen.getByLabelText("Model") as HTMLElement).tagName).toBe("INPUT");

    fireEvent.click(screen.getByRole("button", { name: "Load models from provider" }));
    await screen.findByText(/2 models loaded/);

    // Now it's a real dropdown listing the discovered models.
    const select = screen.getByLabelText("Model") as HTMLSelectElement;
    expect(select.tagName).toBe("SELECT");
    fireEvent.change(select, { target: { value: "provider-model-b" } });
    expect((screen.getByLabelText("Model") as HTMLSelectElement).value).toBe("provider-model-b");

    // Picking "custom" drops back to a text input so an unlisted id can still be entered.
    fireEvent.change(screen.getByLabelText("Model"), { target: { value: "__custom__" } });
    expect((screen.getByLabelText("Model") as HTMLElement).tagName).toBe("INPUT");
    fireEvent.change(screen.getByLabelText("Model"), { target: { value: "my-fine-tune" } });
    expect((screen.getByLabelText("Model") as HTMLInputElement).value).toBe("my-fine-tune");
  });
});
