// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { IntegrationsAdmin } from "./IntegrationsAdmin";

const INSTALLED = [
  {
    id: "local-folder",
    displayName: "Local folder",
    description: "Browse a host directory.",
    authMode: "Service",
    supportsSync: false,
    enabled: false,
    settings: [
      { key: "RootPath", label: "Root path", required: true, isSecret: false, hasValue: false },
    ],
    tools: [
      { name: "list_local_folder", permission: "tools.connectors.local-folder.list_local_folder", requiresApproval: false },
    ],
  },
  {
    id: "azure-blob",
    displayName: "Azure Blob Storage",
    description: "Blob container access.",
    authMode: "Service",
    supportsSync: false,
    enabled: true,
    settings: [
      { key: "ConnectionString", label: "Connection string", required: true, isSecret: true, hasValue: true },
      { key: "Container", label: "Container", required: true, isSecret: false, hasValue: true },
    ],
    tools: [],
  },
];

const AVAILABLE = [
  {
    id: "documenso",
    displayName: "Documenso e-signature",
    description: "Send documents for signature.",
    package: "Plenipo.Connectors",
    registration: "builder.AddPlenipoConnectors()",
  },
];

function stubApi() {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const method = init?.method ?? "GET";
    if (url.includes("/api/admin/connectors") && method === "GET") {
      return Promise.resolve({
        ok: true,
        json: () => Promise.resolve({ installed: INSTALLED, available: AVAILABLE }),
      } as unknown as Response);
    }
    return Promise.resolve({ ok: true, json: () => Promise.resolve(null) } as unknown as Response);
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderIntegrations() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <IntegrationsAdmin />
    </QueryClientProvider>,
  );
}

describe("IntegrationsAdmin", () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
    vi.unstubAllGlobals();
  });

  it("lists installed connectors with their per-tenant enabled state", async () => {
    stubApi();
    renderIntegrations();

    expect(await screen.findByText("Local folder")).toBeTruthy();
    expect(screen.getByText("Azure Blob Storage")).toBeTruthy();
    // local-folder is off (default), so it carries the "disabled" marker.
    expect(screen.getByText("disabled")).toBeTruthy();
  });

  it("shows ecosystem connectors this deployment did not install, with the package to add", async () => {
    stubApi();
    renderIntegrations();

    expect(await screen.findByText("Documenso e-signature")).toBeTruthy();
    expect(screen.getByText("not installed")).toBeTruthy();
    expect(screen.getByText("Plenipo.Connectors")).toBeTruthy();
    // An uninstalled connector offers no per-tenant switch — only the installed two do.
    expect(screen.getAllByRole("switch")).toHaveLength(2);
  });

  it("enables a connector via its switch", async () => {
    const fetchMock = stubApi();
    renderIntegrations();

    await screen.findByText("Local folder");
    fireEvent.click(screen.getAllByRole("switch")[0]); // local-folder, currently off

    await waitFor(() => {
      expect(
        fetchMock.mock.calls.some(
          (c) =>
            String(c[0]).includes("/api/admin/connectors/local-folder/enable") &&
            (c[1] as RequestInit | undefined)?.method === "POST",
        ),
      ).toBe(true);
    });
  });

  it("renders secrets write-only and submits only touched fields", async () => {
    const fetchMock = stubApi();
    renderIntegrations();

    await screen.findByText("Azure Blob Storage");
    fireEvent.click(screen.getAllByRole("button", { name: "Settings" })[1]);

    // The stored secret is never rendered — only the fact that a value exists.
    expect(screen.getByText("value is set")).toBeTruthy();
    const secretInput = screen.getByLabelText(/Connection string/) as HTMLInputElement;
    expect(secretInput.type).toBe("password");
    expect(secretInput.value).toBe("");

    // Change only the container; the untouched secret must not be in the PUT body.
    fireEvent.change(screen.getByLabelText(/Container/), { target: { value: "new-container" } });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    await waitFor(() => {
      const put = fetchMock.mock.calls.find(
        (c) =>
          String(c[0]).includes("/api/admin/connectors/azure-blob/settings") &&
          (c[1] as RequestInit | undefined)?.method === "PUT",
      );
      expect(put).toBeTruthy();
      expect(JSON.parse((put![1] as RequestInit).body as string)).toEqual({
        values: { Container: "new-container" },
      });
    });
  });
});
